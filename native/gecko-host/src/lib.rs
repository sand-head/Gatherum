//! Gecko, driven the way gatherum.js drives a libretro core.
//!
//! core-shim gives a libretro core the flat surface the browser expects. Gecko is not a
//! libretro core: it is a Rust crate with a constructor, a frame loop and two sink traits,
//! and it draws with WebGPU rather than into a buffer. This crate is the same surface
//! built over that shape — the same names, the same integers, so that past `openCore`
//! nothing in gatherum.js or in C# can tell which kind of core it got.
//!
//! Three things are its own business and worth knowing before reading it:
//!
//! **The picture is read back from the GPU.** Gecko's renderer composites the console's
//! output into a texture; every frame that texture is copied into a staging buffer and
//! the buffer is mapped, which on WebGPU completes on a later JavaScript task. So the
//! frame the host hands over is the one *before* the frame it just ran — a lag of one,
//! which is invisible, rather than a stall every frame, which would not be.
//!
//! **The disc lives in this module's memory whole.** A GameCube disc is 1.46 GB, and
//! Gecko reads it out of a `Vec`. That is why the cartridge arrives through `gatherum_alloc`
//! and a copy from JavaScript rather than through the .NET heap, and why the memory
//! ceiling in `.cargo/config.toml` is the most a 32-bit WebAssembly memory can be.
//!
//! **There is no save state.** Gecko has no serializer, so `gatherum_state_size` is zero
//! and a save never reports success. The seam already reads that as a machine that cannot
//! hand itself over, which is the honest answer for a one-player console.

use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};

use backend_wgpu::GxRenderer;
use gecko::audio::AudioSink;
use gecko::flipper::exi::macronix::{ExiMacronix, RTC_SECONDS};
use gecko::flipper::si::pad::{
    self, PadStatus, STICK_CENTER, STICK_MAX, STICK_MIN, TRIGGER_MAX, TRIGGER_MIN,
};
use gecko::flipper::vi::regs::RefreshRate;
use gecko::gekko::wasmjit::{self, BlockCompiler};
use gecko::host::{DrawVertex, GxAction, RenderSink};
use gecko::{GameCube, HostInput};
use wasm_bindgen::prelude::*;

/// The picture is always this size: the width of the console's embedded framebuffer and
/// the tallest picture it can scan out, which is PAL's. A game that draws less — every
/// NTSC one — is centred in it. The seam fixes a picture's size when the cartridge loads,
/// and a GameCube does not say what size it will draw until the game has booted.
const FRAME_WIDTH: u32 = 640;
const FRAME_HEIGHT: u32 = 528;
const ROW_BYTES: u32 = FRAME_WIDTH * 4;

/// What the host reports as its sample rate, whatever the game runs the sound hardware
/// at. A GameCube switches between 32 and 48 kHz per game, and the seam reads the rate
/// once; so the sink resamples to this and the answer never changes.
const SAMPLE_RATE: u32 = 48_000;

/// The IPL mask ROM is 2 MB. What is on it cannot be shipped; its size still matters,
/// because reads wrap at it.
const IPL_ROM_SIZE: usize = 0x20_0000;

/// A "Memory Card 251": 2 MB, the biggest official card, in slot A.
const MEMORY_CARD_BLOCKS: u32 = 256;
const MEMORY_CARD_SLOT: usize = 0;

/// Dolphin's free replacements for the DSP's boot ROM and its coefficient table, fetched
/// by the build. Gecko's sound processor is emulated at the instruction level, so it
/// needs a boot ROM to run at all; Nintendo's cannot be shipped and this one can.
static DSP_ROM: &[u8] = include_bytes!(env!("GATHERUM_DSP_ROM"));
static DSP_COEF: &[u8] = include_bytes!(env!("GATHERUM_DSP_COEF"));

const RVZ_MAGIC: &[u8; 4] = b"RVZ\x01";
const RVZ_DISC_TYPE_OFFSET: usize = 0x48;
const RVZ_DISC_HEADER_OFFSET: usize = 0x58;
const RVZ_DISC_TYPE_GAMECUBE: u32 = 1;
const GC_MAGIC_OFFSET: usize = 0x1C;

// ---- the render sink ------------------------------------------------------------

/// One queued action beside the vertices appended since the previous one — the same
/// arrangement Gecko's own web build uses, because the renderer's `base_vertex` indexing
/// depends on the vertex scratch being rebuilt in exactly the order actions arrive.
struct ActionMessage {
    action: GxAction,
    /// Where this action's new vertices sit in the queue's own vertex buffer. A vector
    /// per action was five thousand allocations a frame on a game drawing a real scene.
    vertices: core::ops::Range<usize>,
}

struct QueueShared {
    messages: Vec<ActionMessage>,
    vertices: Vec<DrawVertex>,
    epoch: u64,
}

type ActionQueue = Arc<Mutex<QueueShared>>;

struct QueueSink {
    shared: ActionQueue,
    scratch: Vec<DrawVertex>,
    scratch_sent_len: usize,
    last_epoch: u64,
}

impl QueueSink {
    fn new(shared: ActionQueue) -> Self {
        Self {
            shared,
            scratch: Vec::new(),
            scratch_sent_len: 0,
            last_epoch: 0,
        }
    }

    fn sync_epoch(&mut self, epoch: u64) {
        if epoch != self.last_epoch {
            self.scratch.clear();
            self.scratch_sent_len = 0;
            self.last_epoch = epoch;
        }
    }
}

impl RenderSink for QueueSink {
    fn exec(&mut self, action: GxAction) {
        let resets = backend_wgpu::sink::action_resets_vertex_scratch(&action);
        {
            let mut shared = self.shared.lock().unwrap();
            if shared.epoch != self.last_epoch {
                self.scratch.clear();
                self.scratch_sent_len = 0;
                self.last_epoch = shared.epoch;
            }
            let start = shared.vertices.len();
            if self.scratch.len() > self.scratch_sent_len {
                let tail = self.scratch_sent_len;
                shared.vertices.extend_from_slice(&self.scratch[tail..]);
            }
            let end = shared.vertices.len();
            self.scratch_sent_len = self.scratch.len();
            shared.messages.push(ActionMessage {
                action,
                vertices: start..end,
            });
        }
        if resets {
            self.scratch.clear();
            self.scratch_sent_len = 0;
        }
    }

    fn vertex_scratch(&mut self) -> &mut Vec<DrawVertex> {
        let epoch = self.shared.lock().unwrap().epoch;
        self.sync_epoch(epoch);
        &mut self.scratch
    }
}

// ---- the audio sink -------------------------------------------------------------

struct AudioShared {
    rate: u32,
    phase: f64,
    previous: (i16, i16),
    samples: Vec<i16>,
}

/// Interleaved stereo at a fixed rate, whatever the game asked the hardware for.
/// Linear interpolation is enough: the two rates are in a ratio of 2:3 and the console's
/// own output stage was not a great deal better.
struct ResamplingSink(Arc<Mutex<AudioShared>>);

impl AudioSink for ResamplingSink {
    fn set_sample_rate(&mut self, sample_rate: u32) {
        if sample_rate > 0 {
            self.0.lock().unwrap().rate = sample_rate;
        }
    }

    fn push_stereo_i16(&mut self, left: i16, right: i16) {
        let mut audio = self.0.lock().unwrap();
        // Bounded: a game that runs while nothing drains would otherwise grow this
        // without limit, and a second of sound is more than a frame's worth of slack.
        if audio.samples.len() > SAMPLE_RATE as usize * 2 {
            audio.samples.clear();
        }
        let step = audio.rate as f64 / SAMPLE_RATE as f64;
        while audio.phase < 1.0 {
            let t = audio.phase;
            let (l, r) = audio.previous;
            audio.samples.push(lerp(l, left, t));
            audio.samples.push(lerp(r, right, t));
            audio.phase += step;
        }
        audio.phase -= 1.0;
        audio.previous = (left, right);
    }
}

fn lerp(from: i16, to: i16, t: f64) -> i16 {
    (from as f64 + (to as f64 - from as f64) * t).round() as i16
}

// ---- the GPU --------------------------------------------------------------------

/// A device, Gecko's renderer on it, and the one buffer the picture comes back through.
struct Gpu {
    device: wgpu::Device,
    queue: wgpu::Queue,
    renderer: GxRenderer,
    staging: wgpu::Buffer,
    /// EFB copies to RAM the GPU has been asked for and this side is waiting to map:
    /// each lands in the game's memory on a later frame, the way the picture does.
    /// Natively the renderer waits on the device for these; a browser cannot wait, so
    /// they are settled here as they arrive, and left unsettled they would pile up a
    /// staging buffer each — which is what a game that copies every frame did.
    writebacks: Vec<(backend_wgpu::PendingWriteback, Rc<Cell<Option<bool>>>)>,
    /// The size of the copy in flight, if one is.
    pending: Option<(u32, u32)>,
    /// None while the copy is in flight; Some(true) once the buffer is mapped and can
    /// be read; Some(false) when mapping failed and the buffer is not mapped at all.
    mapped: Rc<Cell<Option<bool>>>,
    last_size: (u32, u32),
}

impl Gpu {
    async fn new() -> Option<Self> {
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor {
            backends: wgpu::Backends::BROWSER_WEBGPU,
            ..wgpu::InstanceDescriptor::new_without_display_handle()
        });
        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                power_preference: wgpu::PowerPreference::HighPerformance,
                compatible_surface: None,
                force_fallback_adapter: false,
            })
            .await
            .ok()?;
        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor::default())
            .await
            .ok()?;
        // A validation error would otherwise vanish: the picture is read back rather than
        // presented, so nothing on the page ever shows the GPU refusing a command.
        device.on_uncaptured_error(std::sync::Arc::new(|error: wgpu::Error| {
            web_sys::console::warn_1(&format!("GameCube core: {error}").into());
        }));
        // BGRA, because a byte order of B, G, R, A read as a little-endian word is the
        // 0xAARRGGBB pixel the player wants, and the copy is then a copy.
        let renderer = GxRenderer::new(&device, &queue, wgpu::TextureFormat::Bgra8Unorm, 1);
        let staging = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("gatherum_readback"),
            size: (ROW_BYTES * FRAME_HEIGHT) as u64,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        Some(Self {
            device,
            queue,
            renderer,
            staging,
            writebacks: Vec::new(),
            pending: None,
            mapped: Rc::new(Cell::new(None)),
            last_size: (0, 0),
        })
    }

    /// The renderer plays back what the console queued while it ran.
    fn process(&mut self, queue: &ActionQueue) {
        let (messages, vertices) = {
            let mut shared = queue.lock().unwrap();
            shared.epoch = shared.epoch.wrapping_add(1);
            (
                std::mem::take(&mut shared.messages),
                std::mem::take(&mut shared.vertices),
            )
        };
        // Draws are batched until a non-draw action arrives, and a batch left over from the
        // previous frame indexes into the scratch about to be swapped out: flush it first.
        self.renderer.flush_pending_draws(&self.device, &self.queue);
        let mut scratch = self.renderer.replace_vertex_scratch(Vec::new());
        scratch.clear();
        for message in &messages {
            scratch.extend_from_slice(&vertices[message.vertices.clone()]);
            self.renderer.process_action_with_external_scratch(
                &self.device,
                &self.queue,
                &message.action,
                &mut scratch,
            );
        }
        // The queue lent its two vectors for the frame and gets them back emptied, so
        // the next frame's actions and vertices are written into memory already there.
        {
            let mut messages = messages;
            let mut vertices = vertices;
            messages.clear();
            vertices.clear();
            let mut shared = queue.lock().unwrap();
            shared.messages = messages;
            shared.vertices = vertices;
        }
        for writeback in self.renderer.take_pending_writebacks(&self.queue) {
            let mapped = Rc::new(Cell::new(None));
            let flag = mapped.clone();
            writeback
                .staging
                .slice(..writeback.staging_size)
                .map_async(wgpu::MapMode::Read, move |result| {
                    flag.set(Some(result.is_ok()))
                });
            self.writebacks.push((writeback, mapped));
        }
    }

    /// Puts every EFB copy that has mapped since last time into the game's memory.
    fn settle_writebacks(&mut self, mmio: &mut gecko::mmio::Mmio<{ gecko::system::GC }>) {
        if self.writebacks.is_empty() {
            return;
        }
        let mut ram = mmio.ram_view_mut();
        let mut waiting = Vec::new();
        for (writeback, mapped) in self.writebacks.drain(..) {
            match mapped.get() {
                None => waiting.push((writeback, mapped)),
                Some(false) => self.renderer.discard_writeback(writeback),
                Some(true) => {
                    let bytes = writeback
                        .staging
                        .slice(..writeback.staging_size)
                        .get_mapped_range()
                        .to_vec();
                    writeback.staging.unmap();
                    self.renderer.finish_writeback(writeback, &bytes, &mut ram);
                }
            }
        }
        self.writebacks = waiting;
    }

    /// Asks for the picture. It arrives later: see `collect`.
    fn capture(&mut self) {
        if self.pending.is_some() {
            return;
        }
        let texture = &self.renderer.xfb_texture;
        let size = texture.size();
        let (width, height) = (size.width.min(FRAME_WIDTH), size.height.min(FRAME_HEIGHT));
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        encoder.copy_texture_to_buffer(
            wgpu::TexelCopyTextureInfo {
                texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyBufferInfo {
                buffer: &self.staging,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(ROW_BYTES),
                    rows_per_image: None,
                },
            },
            wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
        );
        self.queue.submit([encoder.finish()]);
        self.mapped.set(None);
        let mapped = self.mapped.clone();
        self.staging
            .slice(..)
            .map_async(wgpu::MapMode::Read, move |result| {
                mapped.set(Some(result.is_ok()))
            });
        self.pending = Some((width, height));
    }

    /// Copies the picture asked for last time into the frame, if it has arrived.
    fn collect(&mut self, frame: &mut [u8]) {
        let Some((width, height)) = self.pending else {
            return;
        };
        match self.mapped.get() {
            None => return,
            Some(false) => {
                self.pending = None;
                return;
            }
            Some(true) => {}
        }
        if self.last_size != (width, height) {
            frame.fill(0);
            self.last_size = (width, height);
        }
        {
            let view = self.staging.slice(..).get_mapped_range();
            let x0 = ((FRAME_WIDTH - width) / 2 * 4) as usize;
            let y0 = ((FRAME_HEIGHT - height) / 2) as usize;
            let row_bytes = (width * 4) as usize;
            // The seam's frame is opaque, and what comes back from the texture is not
            // always: a game that never writes alpha leaves it at zero, and a bitmap
            // declared opaque still composites those pixels as nothing.
            for row in 0..height as usize {
                let source = &view[row * ROW_BYTES as usize..][..row_bytes];
                let target = &mut frame[(y0 + row) * ROW_BYTES as usize + x0..][..row_bytes];
                target.copy_from_slice(source);
                for pixel in target.chunks_exact_mut(4) {
                    pixel[3] = 0xFF;
                }
            }
        }
        self.staging.unmap();
        self.pending = None;
    }
}

// ---- the console ----------------------------------------------------------------

/// Gecko's JIT for this target compiles blocks to a WebAssembly module; this is the
/// half that needs a browser. The module is instantiated over this module's own memory
/// and function table, and each of its functions goes into a slot of that table —
/// which, to Rust compiled for wasm32, is exactly what a function pointer is. A slot a
/// block gave up points at a function that only traps until the next block takes it:
/// the table is what keeps a module alive, and a browser has only so much room for
/// executable code.
struct TableCompiler {
    table: js_sys::WebAssembly::Table,
    imports: js_sys::Object,
    placeholder: js_sys::Function,
    free: Vec<u32>,
    complained: bool,
}

impl TableCompiler {
    fn new() -> Option<Self> {
        let table: js_sys::WebAssembly::Table = wasm_bindgen::function_table().dyn_into().ok()?;
        let env = js_sys::Object::new();
        js_sys::Reflect::set(
            &env,
            &wasmjit::MEMORY_IMPORT.into(),
            &wasm_bindgen::memory(),
        )
        .ok()?;
        js_sys::Reflect::set(&env, &wasmjit::TABLE_IMPORT.into(), &table).ok()?;
        let imports = js_sys::Object::new();
        js_sys::Reflect::set(&imports, &wasmjit::IMPORT_MODULE.into(), &env).ok()?;
        let mut compiler = Self {
            table,
            imports,
            placeholder: js_sys::Function::new_no_args(""),
            free: Vec::new(),
            complained: false,
        };
        // A block-shaped function that must never run: a released slot holds it.
        compiler.placeholder = compiler
            .functions_of(&wasmjit::assemble(&[wasmjit::trap()]))
            .ok()?
            .pop()?;
        Some(compiler)
    }

    /// Instantiates the module and returns its exported functions, in order.
    fn functions_of(&self, module: &[u8]) -> Result<Vec<js_sys::Function>, JsValue> {
        let bytes = js_sys::Uint8Array::from(module);
        let module = js_sys::WebAssembly::Module::new(&bytes)?;
        let instance = js_sys::WebAssembly::Instance::new(&module, &self.imports)?;
        let exports = instance.exports();
        let mut functions = Vec::new();
        for i in 0.. {
            let function = js_sys::Reflect::get(&exports, &i.to_string().into())?;
            if function.is_undefined() {
                break;
            }
            functions.push(function.dyn_into()?);
        }
        Ok(functions)
    }

    fn place(&mut self, module: &[u8], count: usize) -> Result<Vec<u32>, JsValue> {
        let functions = self.functions_of(module)?;
        let mut slots = Vec::with_capacity(count);
        for function in functions.iter().take(count) {
            let slot = match self.free.pop() {
                Some(slot) => slot,
                None => self.table.grow(1)?,
            };
            self.table.set(slot, function)?;
            slots.push(slot);
        }
        Ok(slots)
    }
}

impl BlockCompiler for TableCompiler {
    fn compile(&mut self, module: &[u8], functions: usize) -> Option<Vec<u32>> {
        match self.place(module, functions) {
            Ok(slots) if slots.len() == functions => Some(slots),
            Ok(_) => None,
            Err(error) => {
                // The interpreter runs the blocks instead; one complaint is enough.
                if !self.complained {
                    self.complained = true;
                    web_sys::console::warn_1(
                        &format!("GameCube core: could not compile a block: {error:?}").into(),
                    );
                }
                None
            }
        }
    }

    fn release(&mut self, slot: u32) {
        let _ = self.table.set(slot, &self.placeholder);
        self.free.push(slot);
    }
}

struct Console {
    emulator: GameCube,
    actions: ActionQueue,
    audio: Arc<Mutex<AudioShared>>,
    buttons: u32,
    /// The analog sticks as `gatherum_set_sticks` packs them: four signed bytes — left
    /// X, left Y, right X, right Y from the low byte up — positive meaning right and
    /// up, zero a stick at rest.
    sticks: u32,
    /// Frames run since power-on: the console's clock, which counts these and never
    /// the time, so that a cartridge saving the date reads the same date on any replay.
    frames: u64,
}

impl Console {
    fn boot(dvd: Box<dyn image::Dvd>) -> Self {
        let mut emulator = GameCube::with_ipl_hle(dvd);
        emulator.dsp.load_irom(DSP_ROM);
        emulator.dsp.load_coef(DSP_COEF);
        emulator.insert_memory_card(MEMORY_CARD_SLOT, None, MEMORY_CARD_BLOCKS);
        // The device beside the memory card: the IPL mask ROM, the SRAM and the clock.
        // Gecko's IPL-less boot leaves it off the bus, and a game that asks it for the
        // console's settings, or waits on its clock, gets nothing back. The ROM cannot
        // be shipped, so it is blank unless this Gatherum was given one: a font read
        // out of a blank ROM comes out empty, and the SRAM and clock behind it answer
        // as a fresh console would either way. Booting stays the free IPL's job.
        let ipl = IPL_ROM.with(|rom| {
            let rom = rom.borrow();
            if rom.len() == IPL_ROM_SIZE {
                rom.clone()
            } else {
                vec![0u8; IPL_ROM_SIZE]
            }
        });
        emulator.exi.attach_device(
            ExiMacronix::CHANNEL,
            ExiMacronix::DEVICE,
            Box::new(ExiMacronix::new(ipl)),
        );

        let actions: ActionQueue = Arc::new(Mutex::new(QueueShared {
            messages: Vec::new(),
            vertices: Vec::new(),
            epoch: 0,
        }));
        emulator.render_sink = Box::new(QueueSink::new(actions.clone()));

        let audio = Arc::new(Mutex::new(AudioShared {
            rate: SAMPLE_RATE,
            phase: 0.0,
            previous: (0, 0),
            samples: Vec::new(),
        }));
        emulator.audio_sink = Box::new(ResamplingSink(audio.clone()));

        if let Some(compiler) = TableCompiler::new() {
            emulator
                .block_cache
                .get_or_insert_with(Default::default)
                .set_compiler(Some(Box::new(compiler)));
        }

        let mut console = Self {
            emulator,
            actions,
            audio,
            buttons: 0,
            sticks: 0,
            frames: 0,
        };
        console.apply_input();
        console
    }

    /// The pad, from the mask gatherum.js sends in libretro's bit order and the sticks
    /// it packs beside it. A real stick beyond its dead zone steers the main stick and
    /// the C-stick directly; the arrows push the main stick all the way, because that
    /// is what a GameCube game steers with and a keyboard has nothing gentler to offer.
    /// The shoulder buttons pull their triggers all the way, because a game reads the
    /// trigger and only sometimes the click at the end of it; and the button the seam
    /// calls Select is Z, the one button a GameCube pad has that the seam has no other
    /// name for.
    fn apply_input(&mut self) {
        let held = |bit: u32| self.buttons & (1 << bit) != 0;
        let mut buttons = 0u16;
        if held(0) {
            buttons |= pad::B;
        }
        if held(1) {
            buttons |= pad::Y;
        }
        if held(2) {
            buttons |= pad::Z;
        }
        if held(3) {
            buttons |= pad::START;
        }
        if held(8) {
            buttons |= pad::A;
        }
        if held(9) {
            buttons |= pad::X;
        }
        let (left_trigger, right_trigger) = (held(10), held(11));
        if left_trigger {
            buttons |= pad::L;
        }
        if right_trigger {
            buttons |= pad::R;
        }
        let axis = |negative: bool, positive: bool| match (negative, positive) {
            (true, false) => STICK_MIN,
            (false, true) => STICK_MAX,
            _ => STICK_CENTER,
        };
        // A packed byte is ±127 around a centre of 128, which lands just inside the
        // hardware's 0..=255 — a real pad never reaches its own extremes either.
        let stick = |byte: u32| {
            (STICK_CENTER as i32 + (byte as u8 as i8) as i32)
                .clamp(STICK_MIN as i32, STICK_MAX as i32) as u8
        };
        let (left_x, left_y) = (stick(self.sticks), stick(self.sticks >> 8));
        let status = PadStatus {
            buttons,
            stick_x: if left_x != STICK_CENTER {
                left_x
            } else {
                axis(held(6), held(7))
            },
            stick_y: if left_y != STICK_CENTER {
                left_y
            } else {
                axis(held(5), held(4))
            },
            substick_x: stick(self.sticks >> 16),
            substick_y: stick(self.sticks >> 24),
            trigger_left: if left_trigger {
                TRIGGER_MAX
            } else {
                TRIGGER_MIN
            },
            trigger_right: if right_trigger {
                TRIGGER_MAX
            } else {
                TRIGGER_MIN
            },
            connected: true,
        };
        self.emulator.apply_host_input(&HostInput::Gc(status));
    }

    fn fps(&self) -> f64 {
        match self.emulator.vi.dcr.video_format().refresh_rate() {
            RefreshRate::Hz60 => 60.0,
            RefreshRate::Hz50 => 50.0,
        }
    }

    fn memory_card(&mut self) -> Option<&mut [u8]> {
        self.emulator
            .exi
            .memory_card_mut(MEMORY_CARD_SLOT)
            .map(|card| card.data_mut())
    }
}

// ---- the host -------------------------------------------------------------------

struct Host {
    gpu: Option<Gpu>,
    console: Option<Console>,
    frame: Vec<u8>,
    audio_out: Vec<i16>,
    /// What `gatherum_alloc` handed out and how big it was, so `gatherum_free` can give
    /// it back without being told.
    allocations: HashMap<u32, usize>,
}

thread_local! {
    /// The console's boot ROM, handed over before the console powers on. Empty unless
    /// this Gatherum holds one: a blank ROM answers every read with nothing, which is
    /// what a font drawn out of it looks like.
    static IPL_ROM: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };

    static HOST: RefCell<Host> = RefCell::new(Host {
        gpu: None,
        console: None,
        frame: vec![0; (ROW_BYTES * FRAME_HEIGHT) as usize],
        audio_out: Vec::new(),
        allocations: HashMap::new(),
    });
}

fn with_host<T>(f: impl FnOnce(&mut Host) -> T) -> T {
    HOST.with(|host| f(&mut host.borrow_mut()))
}

/// Whether these bytes are a GameCube disc Gecko can boot: a plain image with the disc
/// magic where the console looks for it, or an RVZ whose own header says GameCube.
/// Checked before Gecko sees them, because Gecko trusts what it is given and a trap
/// inside it would take this whole module down with it.
fn is_gamecube_disc(bytes: &[u8]) -> bool {
    let plain = bytes.len() > GC_MAGIC_OFFSET + 4
        && bytes[GC_MAGIC_OFFSET..GC_MAGIC_OFFSET + 4] == image::GC_MAGIC;
    let rvz = bytes.len() > RVZ_DISC_HEADER_OFFSET + 0x80
        && &bytes[..4] == RVZ_MAGIC
        && u32::from_be_bytes(
            bytes[RVZ_DISC_TYPE_OFFSET..RVZ_DISC_TYPE_OFFSET + 4]
                .try_into()
                .unwrap(),
        ) == RVZ_DISC_TYPE_GAMECUBE;
    plain || rvz
}

// ---- the surface ----------------------------------------------------------------
//
// The same names, in the same order, as core-shim's exports. A value crosses as an
// integer or a float and nothing else; a pointer is an offset into this module's memory,
// which wasm-bindgen's loader hands to JavaScript as `memory`.

/// Nothing here is configurable; accepted so the host can be told things it does not
/// need to know.
#[wasm_bindgen]
pub fn gatherum_set_option(_key: u32, _value: u32) {}

/// The console's boot ROM, if this Gatherum holds one, before it powers on. A ROM of
/// any other length is not this console's and is ignored.
#[wasm_bindgen]
pub fn gatherum_set_firmware(address: u32, length: u32) {
    let bytes = unsafe { core::slice::from_raw_parts(address as *const u8, length as usize) };
    if bytes.len() == IPL_ROM_SIZE {
        IPL_ROM.with(|rom| *rom.borrow_mut() = bytes.to_vec());
    }
}

#[wasm_bindgen]
pub fn gatherum_alloc(length: u32) -> u32 {
    let mut buffer = vec![0u8; length as usize];
    let address = buffer.as_mut_ptr() as u32;
    std::mem::forget(buffer);
    with_host(|host| host.allocations.insert(address, length as usize));
    address
}

#[wasm_bindgen]
pub fn gatherum_free(address: u32) {
    if let Some(length) = with_host(|host| host.allocations.remove(&address)) {
        unsafe { drop(Vec::from_raw_parts(address as *mut u8, length, length)) };
    }
}

/// Gecko reads its disc out of memory.
#[wasm_bindgen]
pub fn gatherum_needs_path() -> u32 {
    0
}

#[wasm_bindgen]
pub fn gatherum_load_path(_path: u32) -> u32 {
    0
}

/// Finds a GPU and puts the renderer on it. Asynchronous because WebGPU is, which is why
/// gatherum.js awaits every core's boot.
#[wasm_bindgen]
pub async fn gatherum_boot() -> bool {
    console_error_panic_hook::set_once();
    // Gecko says what it could not do through `tracing`; the browser console is where
    // a person debugging a cartridge looks, the same place the WASI shim's cores print.
    {
        use tracing_subscriber::prelude::*;
        let _ = tracing_subscriber::registry()
            .with(tracing_subscriber::filter::LevelFilter::WARN)
            .with(
                tracing_subscriber::fmt::layer()
                    .with_ansi(false)
                    .without_time()
                    .with_writer(tracing_web::MakeWebConsoleWriter::new()),
            )
            .try_init();
    }
    if with_host(|host| host.gpu.is_some()) {
        return true;
    }
    match Gpu::new().await {
        Some(gpu) => {
            with_host(|host| host.gpu = Some(gpu));
            true
        }
        None => false,
    }
}

/// Takes the disc — the allocation from `gatherum_alloc`, filled in by JavaScript — and
/// boots a console around it. The bytes are not copied: the buffer becomes the disc.
#[wasm_bindgen]
pub fn gatherum_load(address: u32, length: u32) -> u32 {
    let length = length as usize;
    let owned = with_host(|host| host.allocations.remove(&address)) == Some(length);
    if !owned {
        return 0;
    }
    let bytes = unsafe { Vec::from_raw_parts(address as *mut u8, length, length) };
    if !is_gamecube_disc(&bytes) {
        return 0;
    }
    let dvd = image::load_dvd(bytes);
    if !dvd.header().is_gc() {
        return 0;
    }
    with_host(|host| {
        host.console = Some(Console::boot(dvd));
        host.frame.fill(0);
        host.audio_out.clear();
        if let Some(gpu) = host.gpu.as_mut() {
            gpu.last_size = (0, 0);
        }
    });
    1
}

#[wasm_bindgen]
pub fn gatherum_unload() {
    with_host(|host| host.console = None);
}

/// A fresh console around the same disc — Gecko has no reset line, but the disc can be
/// taken out of the dead machine and put into a new one without a second copy.
#[wasm_bindgen]
pub fn gatherum_reset() {
    with_host(|host| {
        let Some(mut console) = host.console.take() else {
            return;
        };
        let Some(dvd) = console.emulator.di.dvd.take() else {
            return;
        };
        let buttons = console.buttons;
        let sticks = console.sticks;
        let card = console.memory_card().map(|data| data.to_vec());
        drop(console);
        let mut fresh = Console::boot(dvd);
        fresh.buttons = buttons;
        fresh.sticks = sticks;
        fresh.apply_input();
        if let (Some(kept), Some(data)) = (card, fresh.memory_card()) {
            let taken = kept.len().min(data.len());
            data[..taken].copy_from_slice(&kept[..taken]);
        }
        host.console = Some(fresh);
        host.frame.fill(0);
        host.audio_out.clear();
        if let Some(gpu) = host.gpu.as_mut() {
            gpu.last_size = (0, 0);
        }
    });
}

#[wasm_bindgen]
pub fn gatherum_set_buttons(port: u32, mask: u32) {
    if port != 0 {
        return;
    }
    with_host(|host| {
        if let Some(console) = host.console.as_mut() {
            console.buttons = mask;
            console.apply_input();
        }
    });
}

/// The analog sticks, packed as core-shim's export of the same name takes them: four
/// signed bytes — left X, left Y, right X, right Y from the low byte up — positive
/// meaning right and up. This is the console the packing exists for: the main stick is
/// how a GameCube game steers and the C-stick is its camera.
#[wasm_bindgen]
pub fn gatherum_set_sticks(port: u32, packed: u32) {
    if port != 0 {
        return;
    }
    with_host(|host| {
        if let Some(console) = host.console.as_mut() {
            if console.sticks == packed {
                return;
            }
            console.sticks = packed;
            console.apply_input();
        }
    });
}

/// One frame: collect the picture the GPU has finished, run the console to its next
/// vertical sync, draw what it queued, ask for that picture, and set out the sound.
#[wasm_bindgen]
pub fn gatherum_run() {
    with_host(|host| {
        let Host {
            gpu,
            console,
            frame,
            audio_out,
            ..
        } = host;
        let (Some(gpu), Some(console)) = (gpu.as_mut(), console.as_mut()) else {
            return;
        };
        gpu.collect(frame);
        gpu.settle_writebacks(&mut console.emulator.mmio);
        console.frames += 1;
        RTC_SECONDS.store(
            (console.frames as f64 / console.fps()) as u32,
            Ordering::Relaxed,
        );
        console.emulator.run_until_vsync();
        gpu.process(&console.actions);
        gpu.capture();
        audio_out.clear();
        audio_out.append(&mut console.audio.lock().unwrap().samples);
    });
}

#[wasm_bindgen]
pub fn gatherum_frame_ptr() -> u32 {
    with_host(|host| host.frame.as_ptr() as u32)
}

#[wasm_bindgen]
pub fn gatherum_frame_width() -> u32 {
    FRAME_WIDTH
}

#[wasm_bindgen]
pub fn gatherum_frame_height() -> u32 {
    FRAME_HEIGHT
}

#[wasm_bindgen]
pub fn gatherum_audio_ptr() -> u32 {
    with_host(|host| host.audio_out.as_ptr() as u32)
}

/// Values, not frames: a stereo pair counts two, as the shim counts them.
#[wasm_bindgen]
pub fn gatherum_audio_len() -> u32 {
    with_host(|host| host.audio_out.len() as u32)
}

#[wasm_bindgen]
pub fn gatherum_fps() -> f64 {
    with_host(|host| host.console.as_ref().map_or(60.0, Console::fps))
}

#[wasm_bindgen]
pub fn gatherum_sample_rate() -> f64 {
    SAMPLE_RATE as f64
}

// A GameCube here has no save state. Every one of these answers "none", in the shape the
// shim answers them, so the seam sees a machine that cannot be handed over rather than
// one that broke.

#[wasm_bindgen]
pub fn gatherum_measure_state() {}

#[wasm_bindgen]
pub fn gatherum_state_size() -> u32 {
    0
}

#[wasm_bindgen]
pub fn gatherum_state_save(_address: u32, _length: u32) {}

#[wasm_bindgen]
pub fn gatherum_state_load(_address: u32, _length: u32) {}

#[wasm_bindgen]
pub fn gatherum_state_ok() -> u32 {
    0
}

/// The memory card's flash: the address is stable for as long as the console runs,
/// because the card never reallocates it, and JavaScript writes a loaded save straight
/// into it the way it writes a cartridge's battery memory.
#[wasm_bindgen]
pub fn gatherum_sram_ptr() -> u32 {
    with_host(|host| {
        host.console
            .as_mut()
            .and_then(Console::memory_card)
            .map_or(0, |data| data.as_ptr() as u32)
    })
}

#[wasm_bindgen]
pub fn gatherum_sram_len() -> u32 {
    with_host(|host| {
        host.console
            .as_mut()
            .and_then(Console::memory_card)
            .map_or(0, |data| data.len() as u32)
    })
}

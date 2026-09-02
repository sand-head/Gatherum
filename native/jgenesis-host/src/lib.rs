//! jgenesis, driven the way gatherum.js drives a libretro core.
//!
//! core-shim gives a libretro core the flat surface the browser expects, and gecko-host
//! gives it to a Rust crate that draws on a GPU. jgenesis is the third shape: a Rust
//! crate — a constructor, a `tick`, and three sink traits for picture, sound and saves —
//! that draws into a buffer of colours. This crate is the same surface built over that,
//! with the same names and the same integers, so that past `openCore` nothing in
//! gatherum.js or in C# can tell which kind of core it got.
//!
//! Three things are its own business and worth knowing before reading it:
//!
//! **The picture is a fixed canvas.** The seam fixes a picture's size when the cartridge
//! loads, and a Mega Drive changes its mind: 256 or 320 pixels wide by register, 224 or
//! 240 lines by region, and twice the lines when a game interlaces. So the frame handed
//! over is always 320 by 240 and whatever the console drew is centred in it — a narrow
//! mode wears a border, and an interlaced field is read every other line.
//!
//! **A save state is bincode, and bincode's size moves.** The seam wants a state to cost
//! the same number of bytes every time; a serialized console does not, quite — a queue
//! here, a FIFO there. So the size reported is the size measured at load plus room to
//! spare, a state is written with its own length in front and zeros behind, and a state
//! that outgrows the room is refused rather than truncated.
//!
//! **The battery memory is reached through a patch.** jgenesis keeps a cartridge's RAM
//! to itself and writes it out through a `SaveWriter`; the seam wants a pointer it can
//! read and write in place. `patches/external-ram.patch` adds the two accessors, and the
//! pointer moves whenever a state is loaded, which is why the seam asks for it every time.

use std::cell::RefCell;
use std::collections::HashMap;
use std::fmt::{self, Display, Formatter};

use bincode::{Decode, Encode};
use genesis_config::{GenesisController, GenesisControllerType, GenesisEmulatorConfig};
use genesis_core::api::GenesisHardware;
use genesis_core::{GenesisButton, GenesisEmulator, GenesisInputs};
use jgenesis_common::frontend::{
    AudioOutput, Color, ConstantInputPoller, EmulatorTrait, FrameSize, MappableInputs,
    RenderFrameOptions, Renderer, SaveWriter, TickEffect,
};
use jgenesis_common::input::Player;
use wasm_bindgen::prelude::*;

/// The widest and tallest picture the console draws without its borders: H40 mode, and
/// the thirty extra lines a PAL console can show.
const FRAME_WIDTH: usize = 320;
const FRAME_HEIGHT: usize = 240;

/// What the host reports as its sample rate. jgenesis resamples its sound chips to
/// whatever it is told, and it is told this once.
const SAMPLE_RATE: u64 = 48_000;

/// Room a state is given over what it measured at load, and the four bytes its real
/// length takes in front of it.
const STATE_HEADROOM: usize = 64 * 1024;
const STATE_LENGTH_BYTES: usize = 4;

/// How many instructions a frame may take before the host stops waiting for one. A
/// frame is a few hundred thousand; a console that never reaches vertical blank would
/// otherwise hang the page.
const MAX_TICKS_PER_FRAME: usize = 8_000_000;

const HEADER_OFFSET: usize = 0x100;
const SEGA_32X_HEADER: &[u8] = b"SEGA 32X";

// ---- the sinks ------------------------------------------------------------------

/// The one error the sinks below can name, and none of them ever returns it: a frame
/// always fits the canvas and sound always fits the queue.
#[derive(Debug)]
struct Refused;

impl Display for Refused {
    fn fmt(&self, f: &mut Formatter<'_>) -> fmt::Result {
        write!(f, "refused")
    }
}

impl std::error::Error for Refused {}

/// The fixed picture, as 0xAARRGGBB pixels the way the seam reads them.
struct Canvas {
    pixels: Vec<u32>,
    last_size: (usize, usize),
}

impl Canvas {
    fn new() -> Self {
        Self { pixels: vec![0xFF00_0000; FRAME_WIDTH * FRAME_HEIGHT], last_size: (0, 0) }
    }

    fn clear(&mut self) {
        self.pixels.fill(0xFF00_0000);
        self.last_size = (0, 0);
    }
}

impl Renderer for Canvas {
    type Err = Refused;

    fn render_frame(
        &mut self,
        frame_buffer: &[Color],
        frame_size: FrameSize,
        _target_fps: f64,
        _options: RenderFrameOptions,
    ) -> Result<(), Refused> {
        let source_width = frame_size.width as usize;
        let source_height = frame_size.height as usize;
        // An interlaced frame is twice as tall; one field of it is the picture.
        let step = if source_height > FRAME_HEIGHT { 2 } else { 1 };
        let width = source_width.min(FRAME_WIDTH);
        let height = (source_height / step).min(FRAME_HEIGHT);
        if self.last_size != (width, height) {
            self.pixels.fill(0xFF00_0000);
            self.last_size = (width, height);
        }
        let x0 = (FRAME_WIDTH - width) / 2;
        let y0 = (FRAME_HEIGHT - height) / 2;
        for row in 0..height {
            let Some(source) = frame_buffer.get(row * step * source_width..).map(|s| &s[..width])
            else {
                break;
            };
            let target = &mut self.pixels[(y0 + row) * FRAME_WIDTH + x0..][..width];
            for (to, from) in target.iter_mut().zip(source) {
                *to = 0xFF00_0000 | (from.r as u32) << 16 | (from.g as u32) << 8 | from.b as u32;
            }
        }
        Ok(())
    }
}

/// Interleaved stereo, sixteen bits a value, at the fixed rate.
struct Speaker(Vec<i16>);

impl AudioOutput for Speaker {
    type Err = Refused;

    fn push_sample(&mut self, left: f64, right: f64) -> Result<(), Refused> {
        // Bounded: a console that runs while nothing drains would otherwise grow this
        // without limit, and a second of sound is more than a frame's worth of slack.
        if self.0.len() > SAMPLE_RATE as usize * 2 {
            self.0.clear();
        }
        self.0.push(sample(left));
        self.0.push(sample(right));
        Ok(())
    }
}

fn sample(value: f64) -> i16 {
    (value * f64::from(i16::MAX)).round().clamp(f64::from(i16::MIN), f64::from(i16::MAX)) as i16
}

/// The save writer that keeps nothing. The seam reads the battery memory straight out of
/// the cartridge and writes a loaded save straight back into it, so a file has nowhere
/// to go and nothing to come from.
struct NoFiles;

impl SaveWriter for NoFiles {
    type Err = Refused;

    fn load_bytes(&mut self, _extension: &str) -> Result<Vec<u8>, Refused> {
        Err(Refused)
    }

    fn persist_bytes(&mut self, _extension: &str, _bytes: &[u8]) -> Result<(), Refused> {
        Ok(())
    }

    fn load_serialized<D: Decode<()>>(&mut self, _extension: &str) -> Result<D, Refused> {
        Err(Refused)
    }

    fn persist_serialized<E: Encode>(&mut self, _extension: &str, _data: E) -> Result<(), Refused> {
        Ok(())
    }
}

// ---- the console ----------------------------------------------------------------

struct Console {
    emulator: GenesisEmulator,
    inputs: GenesisInputs,
    canvas: Canvas,
    speaker: Speaker,
    /// What a state measured when the cartridge loaded, with its headroom: the one size
    /// the seam is ever told.
    state_size: usize,
}

impl Console {
    fn boot(rom: Vec<u8>) -> Option<Self> {
        let hardware = if rom.get(HEADER_OFFSET..HEADER_OFFSET + SEGA_32X_HEADER.len())
            == Some(SEGA_32X_HEADER)
        {
            GenesisHardware::Sega32X
        } else {
            GenesisHardware::Standalone
        };
        let mut emulator = GenesisEmulator::create(
            hardware,
            Some(rom),
            None,
            None,
            GenesisEmulatorConfig::default(),
            &mut NoFiles,
        )
        .ok()?;
        emulator.update_audio_output_frequency(SAMPLE_RATE);

        // Two six-button pads, plugged in. jgenesis leaves the second port empty by
        // default, and a game that looks for a second pad should find one.
        let inputs = GenesisInputs {
            p1: GenesisController::new(GenesisControllerType::SixButton),
            p2: GenesisController::new(GenesisControllerType::SixButton),
        };

        let mut console = Self {
            emulator,
            inputs,
            canvas: Canvas::new(),
            speaker: Speaker(Vec::new()),
            state_size: 0,
        };
        console.state_size = console.encode_state().len() + STATE_LENGTH_BYTES + STATE_HEADROOM;
        Some(console)
    }

    /// The pad, from the mask gatherum.js sends in libretro's bit order. A Mega Drive
    /// pad has three buttons in a row and, on the later one, three more above them; the
    /// seam's positions land on them the way every libretro frontend lands them — B on
    /// B, the right face button on C, the two above on A and Y, the shoulders on X and
    /// Z, and Mode on the button the seam calls Select.
    fn apply_buttons(&mut self, port: u32, mask: u32) {
        let player = match port {
            0 => Player::One,
            1 => Player::Two,
            _ => return,
        };
        const BITS: [(u32, GenesisButton); 12] = [
            (0, GenesisButton::B),
            (1, GenesisButton::A),
            (2, GenesisButton::Mode),
            (3, GenesisButton::Start),
            (4, GenesisButton::Up),
            (5, GenesisButton::Down),
            (6, GenesisButton::Left),
            (7, GenesisButton::Right),
            (8, GenesisButton::C),
            (9, GenesisButton::Y),
            (10, GenesisButton::X),
            (11, GenesisButton::Z),
        ];
        for (bit, button) in BITS {
            self.inputs.set_field(button, player, mask & (1 << bit) != 0);
        }
    }

    /// Runs the console to the end of the frame it is in.
    fn run_frame(&mut self) {
        let Self { emulator, inputs, canvas, speaker, .. } = self;
        let mut poller = ConstantInputPoller(&*inputs);
        for _ in 0..MAX_TICKS_PER_FRAME {
            match emulator.tick(canvas, speaker, &mut poller, &mut NoFiles) {
                Ok(TickEffect::FrameRendered) | Err(_) => return,
                Ok(TickEffect::None) => {}
            }
        }
    }

    fn encode_state(&self) -> Vec<u8> {
        bincode::encode_to_vec(self.emulator.to_save_state(), bincode::config::standard())
            .unwrap_or_default()
    }

    /// The state with its length in front and zeros to the reported size behind, or
    /// nothing when it has outgrown the room it was given.
    fn save_state(&self, room: usize) -> Option<Vec<u8>> {
        let encoded = self.encode_state();
        if room < self.state_size || encoded.len() + STATE_LENGTH_BYTES > room {
            return None;
        }
        let mut state = Vec::with_capacity(room);
        state.extend_from_slice(&(encoded.len() as u32).to_le_bytes());
        state.extend_from_slice(&encoded);
        state.resize(room, 0);
        Some(state)
    }

    fn load_state(&mut self, bytes: &[u8]) -> bool {
        let Some(length) = bytes.get(..STATE_LENGTH_BYTES) else { return false };
        let length = u32::from_le_bytes(length.try_into().unwrap()) as usize;
        let Some(encoded) = bytes.get(STATE_LENGTH_BYTES..STATE_LENGTH_BYTES + length) else {
            return false;
        };
        let Ok((state, _)) =
            bincode::decode_from_slice::<GenesisEmulator, _>(encoded, bincode::config::standard())
        else {
            return false;
        };
        self.emulator.load_state(state);
        // The picture that state was drawing is gone with it; the next frame paints anew.
        self.canvas.clear();
        self.speaker.0.clear();
        true
    }

    fn battery(&mut self) -> Option<&mut [u8]> {
        if !self.emulator.has_sram() {
            return None;
        }
        let ram = self.emulator.external_ram_mut();
        if ram.is_empty() { None } else { Some(ram) }
    }
}

// ---- the host -------------------------------------------------------------------

struct Host {
    console: Option<Console>,
    audio_out: Vec<i16>,
    /// What `gatherum_alloc` handed out and how big it was, so `gatherum_free` can give
    /// it back without being told.
    allocations: HashMap<u32, usize>,
    /// The blank the seam is shown before a cartridge is in, and after one is out.
    blank: Vec<u32>,
    state_ok: bool,
}

thread_local! {
    static HOST: RefCell<Host> = RefCell::new(Host {
        console: None,
        audio_out: Vec::new(),
        allocations: HashMap::new(),
        blank: vec![0xFF00_0000; FRAME_WIDTH * FRAME_HEIGHT],
        state_ok: false,
    });
}

fn with_host<T>(f: impl FnOnce(&mut Host) -> T) -> T {
    HOST.with(|host| f(&mut host.borrow_mut()))
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

/// jgenesis reads its cartridge out of memory.
#[wasm_bindgen]
pub fn gatherum_needs_path() -> u32 {
    0
}

#[wasm_bindgen]
pub fn gatherum_load_path(_path: u32) -> u32 {
    0
}

/// Nothing to find and nothing to wait for: the console is built when the cartridge
/// arrives. A panic is the one thing worth arranging for here, so it reaches the
/// browser console with words rather than as a bare trap.
#[wasm_bindgen]
pub fn gatherum_boot() -> bool {
    console_error_panic_hook::set_once();
    true
}

/// Takes the cartridge — the allocation from `gatherum_alloc`, filled in by JavaScript —
/// and boots a console around it. The bytes are not copied: the buffer becomes the
/// cartridge. A 32X cartridge says so in its header and gets the 32X built around it.
#[wasm_bindgen]
pub fn gatherum_load(address: u32, length: u32) -> u32 {
    let length = length as usize;
    let owned = with_host(|host| host.allocations.remove(&address)) == Some(length);
    if !owned {
        return 0;
    }
    let bytes = unsafe { Vec::from_raw_parts(address as *mut u8, length, length) };
    if bytes.len() <= HEADER_OFFSET + SEGA_32X_HEADER.len() {
        return 0;
    }
    let Some(console) = Console::boot(bytes) else { return 0 };
    with_host(|host| {
        host.console = Some(console);
        host.audio_out.clear();
        host.state_ok = false;
    });
    1
}

#[wasm_bindgen]
pub fn gatherum_unload() {
    with_host(|host| {
        host.console = None;
        host.audio_out.clear();
    });
}

/// The console's own reset button.
#[wasm_bindgen]
pub fn gatherum_reset() {
    with_host(|host| {
        if let Some(console) = host.console.as_mut() {
            console.emulator.soft_reset();
            console.canvas.clear();
            console.speaker.0.clear();
        }
        host.audio_out.clear();
    });
}

#[wasm_bindgen]
pub fn gatherum_set_buttons(port: u32, mask: u32) {
    with_host(|host| {
        if let Some(console) = host.console.as_mut() {
            console.apply_buttons(port, mask);
        }
    });
}

/// A Mega Drive pad has no stick; the value sits here unread.
#[wasm_bindgen]
pub fn gatherum_set_sticks(_port: u32, _packed: u32) {}

/// One frame: run the console to its next vertical blank and set out the sound.
#[wasm_bindgen]
pub fn gatherum_run() {
    with_host(|host| {
        let Host { console, audio_out, .. } = host;
        let Some(console) = console.as_mut() else { return };
        console.run_frame();
        audio_out.clear();
        audio_out.append(&mut console.speaker.0);
    });
}

#[wasm_bindgen]
pub fn gatherum_frame_ptr() -> u32 {
    with_host(|host| {
        host.console
            .as_ref()
            .map_or(host.blank.as_ptr(), |console| console.canvas.pixels.as_ptr()) as u32
    })
}

#[wasm_bindgen]
pub fn gatherum_frame_width() -> u32 {
    FRAME_WIDTH as u32
}

#[wasm_bindgen]
pub fn gatherum_frame_height() -> u32 {
    FRAME_HEIGHT as u32
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

/// What the console is actually running at, which its region decides: a shade under
/// sixty for an NTSC cartridge and a shade under fifty for a PAL one.
#[wasm_bindgen]
pub fn gatherum_fps() -> f64 {
    with_host(|host| host.console.as_ref().map_or(60.0, |console| console.emulator.target_fps()))
}

#[wasm_bindgen]
pub fn gatherum_sample_rate() -> f64 {
    SAMPLE_RATE as f64
}

// A state is worked out in two calls and so is the verdict on it, in the shape the shim
// answers them; nothing here can switch a fiber, but the surface is the surface.

#[wasm_bindgen]
pub fn gatherum_measure_state() {}

#[wasm_bindgen]
pub fn gatherum_state_size() -> u32 {
    with_host(|host| host.console.as_ref().map_or(0, |console| console.state_size as u32))
}

#[wasm_bindgen]
pub fn gatherum_state_save(address: u32, length: u32) {
    with_host(|host| {
        let saved = host
            .console
            .as_ref()
            .and_then(|console| console.save_state(length as usize));
        host.state_ok = match saved {
            Some(state) => {
                let target =
                    unsafe { std::slice::from_raw_parts_mut(address as *mut u8, length as usize) };
                target.copy_from_slice(&state);
                true
            }
            None => false,
        };
    });
}

#[wasm_bindgen]
pub fn gatherum_state_load(address: u32, length: u32) {
    with_host(|host| {
        let source = unsafe { std::slice::from_raw_parts(address as *const u8, length as usize) };
        host.state_ok = host.console.as_mut().is_some_and(|console| console.load_state(source));
        host.audio_out.clear();
    });
}

#[wasm_bindgen]
pub fn gatherum_state_ok() -> u32 {
    with_host(|host| u32::from(host.state_ok))
}

/// The cartridge's battery memory, in place: JavaScript reads a save out of it and writes
/// a loaded one straight into it. The address moves when a state is loaded, because the
/// state brings its own copy, which is why the seam asks for it fresh every time.
#[wasm_bindgen]
pub fn gatherum_sram_ptr() -> u32 {
    with_host(|host| {
        host.console
            .as_mut()
            .and_then(Console::battery)
            .map_or(0, |ram| ram.as_mut_ptr() as u32)
    })
}

#[wasm_bindgen]
pub fn gatherum_sram_len() -> u32 {
    with_host(|host| {
        host.console
            .as_mut()
            .and_then(Console::battery)
            .map_or(0, |ram| ram.len() as u32)
    })
}

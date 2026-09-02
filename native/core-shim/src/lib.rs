//! The wall between a vendored emulator core and the browser.
//!
//! libretro is built out of function pointers: a host hands the core six callbacks and
//! the core calls back into them. JavaScript cannot manufacture a WebAssembly function
//! pointer, so a host written entirely in JS cannot even finish `retro_init`. This crate
//! is the piece that lives on the other side of that wall — compiled into the same
//! module as the core, where a callback is an ordinary function — and it exports a flat
//! surface of plain integers that JavaScript, and through it `IEmulatorCore`, can call.
//!
//! It is `no_std` on purpose. The core it links against brings wasi-libc with it, and a
//! second copy from Rust's standard library would be one too many; everything here works
//! in fixed buffers or borrows the core's own allocator.
//!
//! Nothing in here knows it is mGBA. Any libretro core that links will do.

#![no_std]

use core::cell::UnsafeCell;
use core::ffi::{c_uint, c_void};
use core::panic::PanicInfo;

#[panic_handler]
fn panic(_: &PanicInfo) -> ! {
    // A trap, which the host sees as an exception rather than a wedged frame loop.
    core::arch::wasm32::unreachable()
}

// ---- shared state ---------------------------------------------------------------

/// Interior mutability without a lock. This module is compiled for a WebAssembly build
/// that has no threads: the browser calls in on one thread and the core never spawns
/// another, so the only aliasing risk is a callback firing while we hold a borrow —
/// which is why every access below is short and never held across a call into the core.
struct Shared<T>(UnsafeCell<T>);

unsafe impl<T> Sync for Shared<T> {}

impl<T> Shared<T> {
    const fn new(value: T) -> Self {
        Self(UnsafeCell::new(value))
    }

    /// # Safety
    /// The caller must not hold the returned reference across a call into the core.
    #[allow(clippy::mut_from_ref)]
    unsafe fn get(&self) -> &mut T {
        &mut *self.0.get()
    }
}

/// One video frame's worth of sound at the slowest rate a core is likely to ask for,
/// with room to spare. A frame that somehow produced more is truncated rather than
/// allowed to run off the end.
const AUDIO_CAPACITY: usize = 8192;

struct Video {
    data: *const c_void,
    width: u32,
    height: u32,
    pitch: u32,
}

static VIDEO: Shared<Video> = Shared::new(Video {
    data: core::ptr::null(),
    width: 0,
    height: 0,
    pitch: 0,
});

static AUDIO: Shared<[i16; AUDIO_CAPACITY]> = Shared::new([0; AUDIO_CAPACITY]);
static AUDIO_LEN: Shared<usize> = Shared::new(0);
static BUTTONS: Shared<[u16; 4]> = Shared::new([0; 4]);

/// Each port's analog sticks, packed the way `gatherum_set_sticks` takes them: four
/// signed bytes — left X, left Y, right X, right Y from the low byte up — where
/// positive is right and up. Zero is a pad at rest, which is also what a port that was
/// never told anything answers.
static STICKS: Shared<[u32; 4]> = Shared::new([0; 4]);

/// Answers parked where a second call can fetch them.
///
/// A core whose chips are coroutines — bsnes runs its processor, sound and picture as
/// separate threads — may switch fibers while working out an answer. Where those fibers
/// are the browser's, switching unwinds the stack out to JavaScript and rewinds it back,
/// and **the return value does not survive the trip**: the call completes, every side
/// effect happens, and the caller is handed a zero. Anything that can switch therefore
/// writes its answer down here, and a separate call that cannot switch reads it.
static STATE_SIZE: Shared<usize> = Shared::new(0);
static STATE_OK: Shared<bool> = Shared::new(false);

/// The settings the host has chosen, as pointers to strings the host keeps alive.
///
/// Most of a core's options are taste — which filter, how loud. A few are not: bsnes
/// fills memory with noise at power-on unless told otherwise, which is faithful to the
/// hardware and fatal to two people playing together, because their consoles would start
/// life different. A host that wants machines that agree has to say so.
static OPTIONS: Shared<[(*const u8, *const u8); MAX_OPTIONS]> =
    Shared::new([(core::ptr::null(), core::ptr::null()); MAX_OPTIONS]);
static OPTION_COUNT: Shared<usize> = Shared::new(0);

/// A libretro variable, whose value the host fills in.
#[repr(C)]
struct Variable {
    key: *const u8,
    value: *const u8,
}

/// Compares two NUL-terminated strings. There is no `str` here to lean on: the pointers
/// come from C on one side and JavaScript on the other.
unsafe fn same_text(left: *const u8, right: *const u8) -> bool {
    if left.is_null() || right.is_null() {
        return false;
    }
    let mut at = 0;
    loop {
        let a = *left.add(at);
        let b = *right.add(at);
        if a != b {
            return false;
        }
        if a == 0 {
            return true;
        }
        at += 1;
    }
}

/// The packed picture handed to the host: one opaque 0xAARRGGBB pixel per element, rows
/// tight against each other. A core draws into a buffer of its own choosing, padded to
/// whatever stride suited it, so somebody has to repack — and doing it here costs a copy
/// that the host would otherwise pay with interest across the language boundary.
const MAX_PIXELS: usize = 640 * 480;
static PACKED: Shared<[u32; MAX_PIXELS]> = Shared::new([0; MAX_PIXELS]);

/// The cartridge image, kept alive for as long as the core is running it: libretro does
/// not promise to copy what it is handed.
static ROM: Shared<*mut c_void> = Shared::new(core::ptr::null_mut());

// ---- the core we are compiled against -------------------------------------------

const SET_PIXEL_FORMAT: c_uint = 10;
const GET_VARIABLE: c_uint = 15;
const PIXEL_FORMAT_XRGB8888: c_uint = 1;

/// How many settings a host may hand a core. Cores have dozens; the ones that need
/// answering here are the few that change what the machine *does* rather than how it
/// looks.
const MAX_OPTIONS: usize = 16;
const MEMORY_SAVE_RAM: c_uint = 0;

type Environment = unsafe extern "C" fn(c_uint, *mut c_void) -> bool;
type VideoRefresh = unsafe extern "C" fn(*const c_void, c_uint, c_uint, usize);
type AudioSample = unsafe extern "C" fn(i16, i16);
type AudioBatch = unsafe extern "C" fn(*const i16, usize) -> usize;
type InputPoll = unsafe extern "C" fn();
type InputState = unsafe extern "C" fn(c_uint, c_uint, c_uint, c_uint) -> i16;

#[repr(C)]
struct GameInfo {
    path: *const u8,
    data: *const c_void,
    size: usize,
    meta: *const u8,
}

/// What a core says about itself before anything is loaded. The field that matters here
/// is the last one: some cores read the cartridge out of memory, and others insist on
/// opening a file for themselves.
#[repr(C)]
struct SystemInfo {
    library_name: *const u8,
    library_version: *const u8,
    valid_extensions: *const u8,
    need_fullpath: bool,
    block_extract: bool,
}

#[repr(C)]
struct Geometry {
    base_width: c_uint,
    base_height: c_uint,
    max_width: c_uint,
    max_height: c_uint,
    aspect_ratio: f32,
}

#[repr(C)]
struct Timing {
    fps: f64,
    sample_rate: f64,
}

#[repr(C)]
struct AvInfo {
    geometry: Geometry,
    timing: Timing,
}

extern "C" {
    fn retro_set_environment(cb: Environment);
    fn retro_set_video_refresh(cb: VideoRefresh);
    fn retro_set_audio_sample(cb: AudioSample);
    fn retro_set_audio_sample_batch(cb: AudioBatch);
    fn retro_set_input_poll(cb: InputPoll);
    fn retro_set_input_state(cb: InputState);
    fn retro_init();
    fn retro_reset();
    fn retro_run();
    fn retro_load_game(game: *const GameInfo) -> bool;
    fn retro_unload_game();
    fn retro_get_system_av_info(info: *mut AvInfo);
    fn retro_get_system_info(info: *mut SystemInfo);
    fn retro_serialize_size() -> usize;
    fn retro_serialize(data: *mut c_void, size: usize) -> bool;
    fn retro_unserialize(data: *const c_void, size: usize) -> bool;
    fn retro_get_memory_data(id: c_uint) -> *mut c_void;
    fn retro_get_memory_size(id: c_uint) -> usize;

    // The core's own allocator, borrowed rather than duplicated.
    fn malloc(size: usize) -> *mut c_void;
    fn free(ptr: *mut c_void);
}

// ---- what the core calls back into ----------------------------------------------

/// A host that offers nothing: no system directory, no configuration variables, no log.
/// The one question it answers is the pixel format, and it only says yes to the one
/// layout the packing below knows how to read — a core told yes to a format nobody
/// agreed on would draw a picture that looked almost right.
unsafe extern "C" fn on_environment(command: c_uint, data: *mut c_void) -> bool {
    if data.is_null() {
        return false;
    }
    match command {
        SET_PIXEL_FORMAT => *(data as *const c_uint) == PIXEL_FORMAT_XRGB8888,
        GET_VARIABLE => {
            let asked = &mut *(data as *mut Variable);
            let options = OPTIONS.get();
            for (key, value) in options.iter().take(*OPTION_COUNT.get()) {
                if same_text(*key, asked.key) {
                    asked.value = *value;
                    return true;
                }
            }
            false
        }
        _ => false,
    }
}

unsafe extern "C" fn on_video(data: *const c_void, width: c_uint, height: c_uint, pitch: usize) {
    let video = VIDEO.get();
    video.data = data;
    video.width = width;
    video.height = height;
    video.pitch = pitch as u32;
}

/// Sound arrives in batches during a frame, so it accumulates and the host drains it
/// afterwards. A frame that overruns the buffer is truncated: dropping the tail of one
/// frame's sound is a click, and running off the end is not.
unsafe extern "C" fn on_audio_batch(data: *const i16, frames: usize) -> usize {
    let queue = AUDIO.get();
    let len = AUDIO_LEN.get();
    let values = frames * 2;
    let room = AUDIO_CAPACITY - *len;
    let taken = if values < room { values } else { room };
    core::ptr::copy_nonoverlapping(data, queue.as_mut_ptr().add(*len), taken);
    *len += taken;
    frames
}

unsafe extern "C" fn on_audio(left: i16, right: i16) {
    let pair = [left, right];
    on_audio_batch(pair.as_ptr(), 1);
}

unsafe extern "C" fn on_input_poll() {}

const RETRO_DEVICE_JOYPAD: c_uint = 1;
const RETRO_DEVICE_ANALOG: c_uint = 5;
const ANALOG_INDEX_LEFT: c_uint = 0;
const ANALOG_INDEX_RIGHT: c_uint = 1;
const ANALOG_ID_X: c_uint = 0;
const ANALOG_ID_Y: c_uint = 1;

unsafe extern "C" fn on_input_state(
    port: c_uint,
    device: c_uint,
    index: c_uint,
    id: c_uint,
) -> i16 {
    match device {
        RETRO_DEVICE_JOYPAD => {
            let held = BUTTONS.get();
            match held.get(port as usize) {
                Some(mask) if id < 16 => ((mask >> id) & 1) as i16,
                _ => 0,
            }
        }
        RETRO_DEVICE_ANALOG => {
            let Some(packed) = STICKS.get().get(port as usize) else { return 0 };
            let byte = match (index, id) {
                (ANALOG_INDEX_LEFT, ANALOG_ID_X) => *packed,
                (ANALOG_INDEX_LEFT, ANALOG_ID_Y) => *packed >> 8,
                (ANALOG_INDEX_RIGHT, ANALOG_ID_X) => *packed >> 16,
                (ANALOG_INDEX_RIGHT, ANALOG_ID_Y) => *packed >> 24,
                _ => return 0,
            };
            // A signed byte stretched over libretro's sixteen-bit range: ±127 becomes
            // ±32766. The host says up is positive and libretro says down is, so Y
            // flips on the way through.
            let value = (byte as u8 as i8) as i16 * 258;
            if id == ANALOG_ID_Y { -value } else { value }
        }
        _ => 0,
    }
}

// ---- the surface the host calls -------------------------------------------------

/// Records a setting for the core to find when it asks. Both strings belong to the host
/// and must outlive the core. Call before <c>gatherum_boot</c>: a core reads most of its
/// options once, on the way up.
#[no_mangle]
pub extern "C" fn gatherum_set_option(key: *const u8, value: *const u8) {
    unsafe {
        let count = OPTION_COUNT.get();
        if *count >= MAX_OPTIONS {
            return;
        }
        OPTIONS.get()[*count] = (key, value);
        *count += 1;
    }
}

/// Hands the core its six callbacks and starts it. Must happen before anything else.
#[no_mangle]
pub extern "C" fn gatherum_boot() {
    unsafe {
        retro_set_environment(on_environment);
        retro_set_video_refresh(on_video);
        retro_set_audio_sample(on_audio);
        retro_set_audio_sample_batch(on_audio_batch);
        retro_set_input_poll(on_input_poll);
        retro_set_input_state(on_input_state);
        retro_init();
    }
}

/// Room for the host to write a cartridge into. It is the core's own allocator, so what
/// comes back is memory the core is happy to be handed straight back.
#[no_mangle]
pub extern "C" fn gatherum_alloc(bytes: usize) -> *mut u8 {
    unsafe { malloc(bytes) as *mut u8 }
}

#[no_mangle]
pub extern "C" fn gatherum_free(pointer: *mut u8) {
    unsafe { free(pointer as *mut c_void) }
}

/// Whether this core insists on opening the cartridge itself rather than being handed
/// the bytes. bsnes is one such: it reads the path and ignores the buffer entirely, and
/// reports success either way — so a host that guesses wrong gets a console that loaded
/// nothing and says nothing.
#[no_mangle]
pub extern "C" fn gatherum_needs_path() -> bool {
    unsafe {
        let mut info = SystemInfo {
            library_name: core::ptr::null(),
            library_version: core::ptr::null(),
            valid_extensions: core::ptr::null(),
            need_fullpath: false,
            block_extract: false,
        };
        retro_get_system_info(&mut info);
        info.need_fullpath
    }
}

/// For a core that wants a path: the host has already put the cartridge somewhere the
/// core can open it, and hands over the name it filed it under.
#[no_mangle]
pub extern "C" fn gatherum_load_path(path: *const u8) -> bool {
    unsafe {
        gatherum_unload();
        let info = GameInfo {
            path,
            data: core::ptr::null(),
            size: 0,
            meta: core::ptr::null(),
        };
        retro_load_game(&info)
    }
}

/// Takes ownership of the cartridge bytes: libretro does not promise to copy what it is
/// given, so the buffer stays alive here until the game is unloaded.
#[no_mangle]
pub extern "C" fn gatherum_load(data: *mut u8, bytes: usize) -> bool {
    unsafe {
        gatherum_unload();
        let info = GameInfo {
            path: core::ptr::null(),
            data: data as *const c_void,
            size: bytes,
            meta: core::ptr::null(),
        };
        if !retro_load_game(&info) {
            free(data as *mut c_void);
            return false;
        }
        *ROM.get() = data as *mut c_void;
        true
    }
}

#[no_mangle]
pub extern "C" fn gatherum_unload() {
    unsafe {
        let rom = ROM.get();
        if rom.is_null() {
            return;
        }
        retro_unload_game();
        free(*rom);
        *rom = core::ptr::null_mut();
    }
}

#[no_mangle]
pub extern "C" fn gatherum_reset() {
    unsafe { retro_reset() }
}

/// One frame: the sound from the last one is dropped first, because the host has had
/// its chance to take it and a frame's sound belongs to that frame.
#[no_mangle]
pub extern "C" fn gatherum_run() {
    unsafe {
        *AUDIO_LEN.get() = 0;
        retro_run();
        pack_frame();
    }
}

/// Copies the core's picture into a tight, opaque buffer. A core draws at whatever
/// stride suited it and leaves the top byte of each pixel undefined; both are this
/// function's problem rather than the host's.
unsafe fn pack_frame() {
    let video = VIDEO.get();
    if video.data.is_null() {
        return;
    }
    let width = video.width as usize;
    let height = video.height as usize;
    let stride = (video.pitch / 4) as usize;
    if width == 0 || height == 0 || width > stride || width * height > MAX_PIXELS {
        return;
    }

    let source = video.data as *const u32;
    let packed = PACKED.get().as_mut_ptr();
    for row in 0..height {
        let from = source.add(row * stride);
        let to = packed.add(row * width);
        for column in 0..width {
            *to.add(column) = *from.add(column) | 0xFF00_0000;
        }
    }
}

#[no_mangle]
pub extern "C" fn gatherum_frame_ptr() -> *const u32 {
    unsafe { PACKED.get().as_ptr() }
}

#[no_mangle]
pub extern "C" fn gatherum_frame_width() -> u32 {
    unsafe { VIDEO.get().width }
}

#[no_mangle]
pub extern "C" fn gatherum_frame_height() -> u32 {
    unsafe { VIDEO.get().height }
}

#[no_mangle]
pub extern "C" fn gatherum_audio_ptr() -> *const i16 {
    unsafe { AUDIO.get().as_ptr() }
}

/// How many values — not frames — the last run produced. Sound is interleaved, two per
/// moment, which is what the player's own stereo path already expects.
#[no_mangle]
pub extern "C" fn gatherum_audio_len() -> usize {
    unsafe { *AUDIO_LEN.get() }
}

#[no_mangle]
pub extern "C" fn gatherum_set_buttons(port: u32, mask: u32) {
    unsafe {
        if let Some(held) = BUTTONS.get().get_mut(port as usize) {
            *held = mask as u16;
        }
    }
}

/// The port's analog sticks, four signed bytes in one integer — left X, left Y, right
/// X, right Y from the low byte up, positive meaning right and up. A core without a
/// stick never queries the analog device and the value sits here unread.
#[no_mangle]
pub extern "C" fn gatherum_set_sticks(port: u32, packed: u32) {
    unsafe {
        if let Some(sticks) = STICKS.get().get_mut(port as usize) {
            *sticks = packed;
        }
    }
}

// ---- what the machine says about itself ------------------------------------------

/// Valid only once a game is loaded, which is libretro's rule and not ours.
unsafe fn av_info() -> AvInfo {
    let mut info = AvInfo {
        geometry: Geometry {
            base_width: 0,
            base_height: 0,
            max_width: 0,
            max_height: 0,
            aspect_ratio: 0.0,
        },
        timing: Timing {
            fps: 0.0,
            sample_rate: 0.0,
        },
    };
    retro_get_system_av_info(&mut info);
    info
}

#[no_mangle]
pub extern "C" fn gatherum_fps() -> f64 {
    unsafe { av_info().timing.fps }
}

#[no_mangle]
pub extern "C" fn gatherum_sample_rate() -> f64 {
    unsafe { av_info().timing.sample_rate }
}

// ---- state, and the memory a battery would have kept -----------------------------

/// Works out what a state costs and parks the answer. Returns nothing on purpose: the
/// fiber swap this may go through would lose it.
#[no_mangle]
pub extern "C" fn gatherum_measure_state() {
    unsafe {
        *STATE_SIZE.get() = retro_serialize_size();
    }
}

/// What the last measurement found. Touches nothing that can switch a fiber, so it can
/// be trusted to answer.
#[no_mangle]
pub extern "C" fn gatherum_state_size() -> usize {
    unsafe { *STATE_SIZE.get() }
}

#[no_mangle]
pub extern "C" fn gatherum_state_save(data: *mut u8, bytes: usize) {
    unsafe {
        *STATE_OK.get() = retro_serialize(data as *mut c_void, bytes);
    }
}

#[no_mangle]
pub extern "C" fn gatherum_state_load(data: *const u8, bytes: usize) {
    unsafe {
        *STATE_OK.get() = retro_unserialize(data as *const c_void, bytes);
    }
}

/// Whether the last save or load actually worked.
#[no_mangle]
pub extern "C" fn gatherum_state_ok() -> bool {
    unsafe { *STATE_OK.get() }
}

#[no_mangle]
pub extern "C" fn gatherum_sram_ptr() -> *mut u8 {
    unsafe { retro_get_memory_data(MEMORY_SAVE_RAM) as *mut u8 }
}

#[no_mangle]
pub extern "C" fn gatherum_sram_len() -> usize {
    unsafe { retro_get_memory_size(MEMORY_SAVE_RAM) }
}

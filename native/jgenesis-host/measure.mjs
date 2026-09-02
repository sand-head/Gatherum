// The measurement behind the Mega Drive's player count.
//
// A vendored core's determinism is somebody else's claim, and the answer to a claim is a
// measurement: two copies of the core, the same cartridge, the same buttons on the same
// frames, and their states compared every sixty frames over six hundred — plus a third
// copy given different buttons, to prove the agreement is not a core ignoring its pads.
// This runs that against native/dist/jgenesis.mjs under Node, the way the bsnes one was
// run, and prints a verdict a person can read.
//
//   node native/jgenesis-host/measure.mjs          # after ./native/build-core.sh jgenesis
//
// The cartridge is assembled here rather than checked in: a real one is somebody's game.
// It is a small program that reads both pads every frame, keeps a running sum of what
// they said in work RAM, paints the background with the sum and pokes the PSG with it,
// then waits for the next vertical blank — so a state depends on the buttons and on
// nothing else, and the picture shows the console is drawing.
//
// This is tooling beside build-core.sh, not code the wiki serves: it never leaves this
// directory.
import fs from "node:fs";

function megaDriveRom() {
  const rom = new Uint8Array(0x10000);
  const dv = new DataView(rom.buffer);
  dv.setUint32(0, 0x00FFFE00); dv.setUint32(4, 0x200);
  for (let v = 2; v < 64; v++) dv.setUint32(v * 4, 0x2D4);
  const text = (at, s) => { for (let i = 0; i < s.length; i++) rom[at + i] = s.charCodeAt(i); };
  rom.fill(0x20, 0x100, 0x200);
  text(0x100, "SEGA MEGA DRIVE "); text(0x110, "(C)GATHERUM 2026"); text(0x150, "DETERMINISM PROBE");
  text(0x180, "GM 00000000-00"); text(0x1F0, "JUE");
  dv.setUint32(0x1A4, rom.length - 1); dv.setUint32(0x1A8, 0xFF0000); dv.setUint32(0x1AC, 0xFFFFFF);
  const words = [
    0x23FC,0x5345,0x4741,0x00A1,0x4000,           // move.l #'SEGA',$A14000
    0x13FC,0x0040,0x00A1,0x0009,                  // move.b #$40,$A10009
    0x13FC,0x0040,0x00A1,0x000B,                  // move.b #$40,$A1000B
    0x33FC,0x8004,0x00C0,0x0004,                  // vdp reg 0
    0x33FC,0x8144,0x00C0,0x0004,                  // vdp reg 1: display on, mode 5
    0x33FC,0x8C81,0x00C0,0x0004,                  // vdp reg 12: H40
    0x33FC,0x8F02,0x00C0,0x0004,                  // vdp reg 15: autoincrement 2
    0x7800,                                       // moveq #0,d4
    // loop (0x23C)
    0x13FC,0x0040,0x00A1,0x0003, 0x4E71,0x4E71, 0x1039,0x00A1,0x0003,
    0x13FC,0x0000,0x00A1,0x0003, 0x4E71,0x4E71, 0x1239,0x00A1,0x0003,
    0xE149, 0x8041, 0x33C0,0x00FF,0x0000, 0xD840,
    0x13FC,0x0040,0x00A1,0x0005, 0x4E71,0x4E71, 0x1039,0x00A1,0x0005,
    0x13FC,0x0000,0x00A1,0x0005, 0x4E71,0x4E71, 0x1239,0x00A1,0x0005,
    0xE149, 0x8041, 0x33C0,0x00FF,0x0002, 0xD840,
    0x33C4,0x00FF,0x0004,                         // move.w d4,$FF0004
    0x23FC,0xC000,0x0000,0x00C0,0x0004,           // CRAM address 0
    0x33C4,0x00C0,0x0000,                         // move.w d4,$C00000
    0x13C4,0x00C0,0x0011,                         // move.b d4,$C00011 (PSG)
    0x3A39,0x00C0,0x0004, 0x0805,0x0003, 0x67F4,  // wait for vblank
    0x3A39,0x00C0,0x0004, 0x0805,0x0003, 0x66F4,  // wait for it to end
    0x6000,0xFF6A,                                // bra loop
    0x4E73,                                       // rte (0x2D4)
  ];
  let at = 0x200;
  for (const w of words) { dv.setUint16(at, w); at += 2; }
  if (at - 2 !== 0x2D4) throw new Error("rte landed at " + (at - 2).toString(16));
  return rom;
}

const dist = process.argv[2] ?? new URL("../dist", import.meta.url).pathname;
const wasm = fs.readFileSync(dist + "/jgenesis_bg.wasm");

async function machine(tag) {
  const glue = await import(dist + "/jgenesis.mjs?" + tag);
  const started = await glue.default({ module_or_path: wasm });
  const bytes = () => new Uint8Array(started.memory.buffer);
  glue.gatherum_boot();
  const rom = megaDriveRom();
  const address = glue.gatherum_alloc(rom.length); bytes().set(rom, address);
  if (!glue.gatherum_load(address, rom.length)) throw new Error("load failed");
  glue.gatherum_run(); glue.gatherum_measure_state();
  const stateSize = glue.gatherum_state_size();
  const scratch = glue.gatherum_alloc(stateSize);
  return {
    glue, bytes, stateSize,
    facts: () => ({ w: glue.gatherum_frame_width(), h: glue.gatherum_frame_height(), fps: glue.gatherum_fps(),
      rate: glue.gatherum_sample_rate(), state: stateSize, sram: glue.gatherum_sram_len(), audio: glue.gatherum_audio_len() }),
    run: (p1, p2) => { glue.gatherum_set_buttons(0, p1); glue.gatherum_set_buttons(1, p2); glue.gatherum_run(); },
    state: () => { glue.gatherum_state_save(scratch, stateSize); if (!glue.gatherum_state_ok()) throw new Error("save failed");
      return Buffer.from(bytes().slice(scratch, scratch + stateSize)); },
    load: (buf) => { bytes().set(buf, scratch); glue.gatherum_state_load(scratch, stateSize); return !!glue.gatherum_state_ok(); },
    frame: () => new Uint32Array(started.memory.buffer, glue.gatherum_frame_ptr(), 320 * 240),
    ram: () => { const view = new DataView(started.memory.buffer); return view; },
  };
}

const a = await machine("a"), b = await machine("b"), c = await machine("c");
console.log("facts:", a.facts());

// Scripted buttons: a fixed pseudo-random sequence, the same for a and b, different for c.
let seed = 0x1234;
const next = () => { seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF; return seed >>> 8; };
const frames = 600;
let mismatches = 0;
const t0 = performance.now();
for (let frame = 1; frame <= frames; frame++) {
  const p1 = next() & 0xFFF, p2 = next() & 0xFFF;
  a.run(p1, p2); b.run(p1, p2); c.run(p1 ^ 0x10, p2);
  if (frame % 60 === 0) {
    const sa = a.state(), sb = b.state(), sc = c.state();
    const same = sa.equals(sb), control = sa.equals(sc);
    if (!same) mismatches++;
    console.log(`frame ${frame}: a==b ${same}  a==c ${control}`);
  }
}
console.log(`${frames} frames x3 in ${(performance.now() - t0).toFixed(0)} ms; mismatches between a and b: ${mismatches}`);

// The picture depends on the buttons: colour 0 is the running sum.
const frame = a.frame();
let nonblack = 0; for (const p of frame) if ((p & 0xFFFFFF) !== 0) nonblack++;
console.log("non-black pixels:", nonblack, "corner pixel:", frame[0].toString(16), "audio values last frame:", a.glue.gatherum_audio_len());

// A state round-trips: load a's state into c, run both the same, compare.
console.log("state loaded into c:", c.load(a.state()));
for (let i = 0; i < 60; i++) { const p = next() & 0xFFF; a.run(p, 0); c.run(p, 0); }
console.log("after loading a's state into c and running 60 frames alike: a==c", a.state().equals(c.state()));

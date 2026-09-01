// Two libco calls that its Emscripten fiber backend does not implement, supplied as a
// translation unit of its own so the core's own source stays byte-for-byte what its
// licence points at.
#include <libco.h>

// Emscripten fibers own the stacks they run on, so there is no building one over memory
// somebody else already holds. bsnes uses this in one place — recycling a thread it made
// earlier when the console is reset — so letting the old fiber go and making a fresh one
// is the same thing by another route.
cothread_t co_derive(void* existing, unsigned int size, void (*entry)(void)) {
  if (existing) {
    co_delete((cothread_t)existing);
  }
  return co_create(size, entry);
}

// Whether a coroutine's stack can be captured byte for byte. An Emscripten fiber's stack
// is not in linear memory, so it cannot — and saying so is what makes bsnes take its
// safer path, running every thread to a synchronisation point before it serializes
// rather than photographing them mid-stride.
int co_serializable(void) {
  return 0;
}

// ---- and one clock that must not tell the time ----------------------------------

#include <time.h>

// bsnes seeds its randomness with clock() at power-on. With entropy switched off that
// seed is never used for anything — but it is still carried in a save state, so two
// consoles that should be identical differ by eight bytes for ever, and a desync check
// comparing states would cry wolf on every frame. A core that reads a real clock cannot
// stay in step with a copy of itself in somebody else's browser; answering with a
// constant is the same discipline the WASI host applies to clock_time_get.
clock_t clock(void) {
  return 0;
}

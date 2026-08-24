# Icons

Gatherum's icons are [Lucide](https://lucide.dev) — ISC, and already the app's house
style: the inline `<svg>`s in `TreeItem`, `HeaderActions`, `SearchBox` and
`NodeHeader` are Lucide shapes drawn straight into the markup, which is the right way to
do it when there is markup to draw into. `LICENSE` beside this file is the pack's, kept
here because the icons in this folder are redistributed with the app.

This folder is the other case: an icon CSS has to draw because there is nowhere to put an
`<svg>`. A page rendered by slopedit is made of styled *text runs*, so the padlock on a
link the reader may not follow can only arrive as a background on a pseudo-element:

```css
background-color: currentColor;
mask: url("/icons/lock.svg") no-repeat center / contain;
```

Masking rather than `background-image` is what keeps `currentColor` working — an SVG
referenced as an image can't see the page's colour, but its alpha channel makes a
perfectly good stencil, and the fill under it is the app's.

## Adding one

Copy the file out of the pack at a pinned version, keep its provenance comment, and name
it after the icon:

```sh
npm pack lucide-static@1.33.0 && tar xzf lucide-static-1.33.0.tgz package/icons/<name>.svg
```

Anything with real markup around it should stay an inline `<svg>` from the same pack —
same shapes, one less request, and `stroke="currentColor"` works directly there.

# UI design

The desktop app's design is an HTML mockup in [`mockup/`](mockup/). The app (`src/MusicSpin.App`, Avalonia 12) is built from it; the work is tracked in the GitHub issues labelled [`area: app`](https://github.com/vgabi94/MusicSpin/issues?q=label%3A%22area%3A+app%22).

## Opening it

Static files, no build step:

```sh
cd docs/design/mockup
python -m http.server 8765
```

Then open <http://127.0.0.1:8765/> for the app, and <http://127.0.0.1:8765/decks/> for the turntable designs that Now Playing can use.

Deep links put the mockup in a given state, for example `?view=crate&group=genre`, `?play=1&np=1&light=1` or `?page=radio&station=genre:Jazz&radio=1`. The full list is in the comment above `new URLSearchParams` at the end of `app.js`.

## What is real and what is faked

- **Faked:** the library (`data.js`: 24 albums with generated gradient covers and generated track lists), playback (a timer), and the spectrum analyzer's levels. The app uses the real library, cover art and the background player.
- **Real:** the layout, sizes, colours (dark and light themes), animations and their timings, and the behaviour of every control.

The issues record decisions taken after the mockup. Where an issue and the mockup disagree, the issue wins.

## Files

| File | What it holds |
|---|---|
| `index.html` | The window: title bar, sidebar, content, Now Playing, queue drawer, player bar, Settings |
| `styles.css` | Theme tokens and one commented section per area (`/* ---------- Player bar ---------- */`) |
| `app.js` | Behaviour, one commented section per area |
| `data.js` | The mock library |
| `decks/` | Six turntable designs (`1-walnut-brass.html` … `6-smoked-acrylic.html`) on a shared bench (`kit.css`, `kit.js`), and a gallery (`index.html`) |

// Mock library. Covers are generated from a palette + pattern, so the mockup needs no image files.
window.ALBUMS = [
  { title: "Low Tide Static",   artist: "Mara Velde",        year: 2021, genre: "Electronic", fmt: "FLAC", res: "24/96",   pal: ["#0f2a44", "#e86a3c", "#f3d9b1"], pat: "sun" },
  { title: "Paper Lanterns",    artist: "The Quiet Orchard", year: 2019, genre: "Indie",      fmt: "FLAC", res: "16/44.1", pal: ["#f2e3c6", "#c0392b", "#2c3e50"], pat: "stripes" },
  { title: "Night Transit",     artist: "Kodo Arai",         year: 2023, genre: "Jazz",       fmt: "FLAC", res: "24/192",  pal: ["#111827", "#38bdf8", "#f472b6"], pat: "grid" },
  { title: "Hollow Hills",      artist: "Sól & Ash",         year: 2018, genre: "Folk",       fmt: "ALAC", res: "24/48",   pal: ["#3f4a3c", "#d9c8a0", "#8a5a3b"], pat: "rings" },
  { title: "Glass Garden",      artist: "Ilse Morrow",       year: 2022, genre: "Classical",  fmt: "DSD64", res: "1-bit/2.8M", pal: ["#e9f1f7", "#6c8ea4", "#1f3a4d"], pat: "wave" },
  { title: "Concrete Bloom",    artist: "FRAMEWORK",         year: 2020, genre: "Electronic", fmt: "FLAC", res: "24/44.1", pal: ["#d4d4d4", "#ff3b30", "#1c1c1c"], pat: "split" },
  { title: "Velvet Hours",      artist: "Ana Cruz Quintet",  year: 2017, genre: "Jazz",       fmt: "FLAC", res: "24/96",   pal: ["#3b0d2e", "#d4a373", "#f8e1c4"], pat: "dots" },
  { title: "Northern Lines",    artist: "Halvard Eik",       year: 2024, genre: "Ambient",    fmt: "FLAC", res: "24/96",   pal: ["#0b3d3a", "#7fffd4", "#c3f0ff"], pat: "aurora" },
  { title: "Sundial",           artist: "Peach Motel",       year: 2016, genre: "Indie",      fmt: "MP3",  res: "320k",    pal: ["#ffcf56", "#ff6f59", "#254441"], pat: "sun" },
  { title: "Static Choir",      artist: "Vessel Unit",       year: 2021, genre: "Electronic", fmt: "WAV",  res: "24/48",   pal: ["#1a1a2e", "#e94560", "#0f3460"], pat: "stripes" },
  { title: "Saltwater Hymns",   artist: "June Harrow",       year: 2015, genre: "Folk",       fmt: "FLAC", res: "16/44.1", pal: ["#cfe3e8", "#2a6f97", "#012a4a"], pat: "wave" },
  { title: "Mono Rêve",         artist: "Les Patients",      year: 2019, genre: "Indie",      fmt: "Opus", res: "96k", ch: 1, pal: ["#f5f0e6", "#111111", "#d62828"], pat: "grid" },
  { title: "Cello Suites",      artist: "Elena Varga",       year: 2020, genre: "Classical",  fmt: "FLAC", res: "24/192",  pal: ["#2b2118", "#c9a227", "#efe6d0"], pat: "rings" },
  { title: "Afterglow Engine",  artist: "Neon Parish",       year: 2022, genre: "Electronic", fmt: "FLAC", res: "24/48",   pal: ["#240046", "#ff9e00", "#ff006e"], pat: "sun" },
  { title: "Field Notes",       artist: "Tomas Lind",        year: 2018, genre: "Ambient",    fmt: "FLAC", res: "24/96",   pal: ["#e8e2d0", "#6b705c", "#a5a58d"], pat: "dots" },
  { title: "Blue Interval",     artist: "The Harlan Trio",   year: 1962, genre: "Jazz",       fmt: "FLAC", res: "24/192",  pal: ["#0a2463", "#3e92cc", "#fffaff"], pat: "split" },
  // more albums by artists above, so grouping by artist has real groups
  { title: "Harbor Lights",     artist: "Mara Velde",        year: 2018, genre: "Electronic", fmt: "FLAC", res: "24/48",   pal: ["#16324f", "#f6ae2d", "#e6e6e6"], pat: "sun" },
  { title: "Ember Season",      artist: "Mara Velde",        year: 2024, genre: "Electronic", fmt: "FLAC", res: "24/96",   pal: ["#2b0f0e", "#f26a4f", "#f6d6ad"], pat: "rings" },
  { title: "Rain Districts",    artist: "Kodo Arai",         year: 2020, genre: "Jazz",       fmt: "FLAC", res: "24/96",   pal: ["#0d1b2a", "#9d4edd", "#e0aaff"], pat: "stripes" },
  { title: "Orchard Light",     artist: "The Quiet Orchard", year: 2023, genre: "Indie",      fmt: "FLAC", res: "24/48",   pal: ["#fdf0d5", "#6a994e", "#386641"], pat: "dots" },
  { title: "Partitas",          artist: "Elena Varga",       year: 2023, genre: "Classical",  fmt: "FLAC", res: "24/96",   pal: ["#1b1b1b", "#b08968", "#ede0d4"], pat: "split" },
  { title: "Live at the Lantern", artist: "The Harlan Trio", year: 1964, genre: "Jazz",       fmt: "FLAC", res: "24/96",   pal: ["#3d0c02", "#e09f3e", "#fff3b0"], pat: "grid" },
  // older decades, so radio has more than three decades to pick from
  { title: "Copper Sun",        artist: "Delta Ruth",        year: 1973, genre: "Folk",       fmt: "FLAC", res: "24/96",   pal: ["#5c2a0d", "#e9a23b", "#f6e7c1"], pat: "sun" },
  { title: "Pulse Theory",      artist: "FRAMEWORK",         year: 1997, genre: "Electronic", fmt: "MP3",  res: "320k",    pal: ["#0f0f0f", "#00e5ff", "#ff2e63"], pat: "grid" },
];

// Deterministic fake track lists.
(function () {
  const words = ["Morning", "Signal", "Harbor", "Ember", "Quiet", "Drift", "Arc", "Lantern", "Echo", "Meridian",
    "Salt", "Orbit", "Paper", "Velvet", "Thread", "Window", "Coast", "Hollow", "Tide", "Glass", "Return", "Bloom"];
  let seed = 7;
  const rnd = () => (seed = (seed * 9301 + 49297) % 233280) / 233280;
  const pick = () => words[Math.floor(rnd() * words.length)];
  for (const a of window.ALBUMS) {
    const n = 6 + Math.floor(rnd() * 5);
    a.tracks = Array.from({ length: n }, (_, i) => ({
      no: i + 1,
      title: rnd() < 0.5 ? `${pick()} ${pick()}` : `The ${pick()}`,
      dur: 120 + Math.floor(rnd() * 300),
    }));
  }
})();

// What the library reads from each file for the player bar: bitrate per track (lossless compresses differently
// from track to track; lossy is the encoder's nominal rate), and the sample rate a lossy file doesn't name in `res`.
(function () {
  let seed = 3;
  const rnd = () => (seed = (seed * 9301 + 49297) % 233280) / 233280;
  for (const al of window.ALBUMS) {
    const ch = al.ch || 2, lossy = al.res.endsWith("k");
    if (lossy) al.khz = al.fmt === "Opus" ? 48 : 44.1;
    const [bits, rate] = al.res.split("/"), khz = rate?.endsWith("M") ? parseFloat(rate) * 1000 : parseFloat(rate);
    const raw = lossy ? parseInt(al.res) : al.fmt.startsWith("DSD") ? 2822.4 * ch : parseInt(bits) * khz * ch;
    const packed = al.fmt === "FLAC" || al.fmt === "ALAC";
    for (const t of al.tracks) t.kbps = Math.round(packed ? raw * (0.5 + rnd() * 0.22) : raw);
  }
})();

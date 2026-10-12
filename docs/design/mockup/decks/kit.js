// Shared bench for the deck + needle studies: the record (album cover as label), the spin clock,
// and the same play / stop sequence the app uses (classes spinning, arm-on, arm-down on .deck).
// ?embed hides the caption and controls (gallery), ?light uses the light theme, ?album=N picks the record.
(() => {
  const qs = new URLSearchParams(location.search);
  const root = document.documentElement;
  if (qs.has("embed")) root.classList.add("embed");
  if (qs.has("light")) root.classList.add("light");
  const albums = window.ALBUMS;
  const deck = document.querySelector(".deck"), vinyl = deck.querySelector(".vinyl");
  const playBtn = document.querySelector("#play");
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));

  function coverBg({ pal: [a, b, c], pat }) {
    switch (pat) {
      case "sun":    return `radial-gradient(circle at 50% 56%, ${b} 0 21%, transparent 21.5%), linear-gradient(${a} 0 62%, ${c} 62%)`;
      case "stripes":return `radial-gradient(circle at 70% 32%, ${c} 0 16%, transparent 16.5%), repeating-linear-gradient(-35deg, ${b} 0 7%, ${a} 7% 14%)`;
      case "grid":   return `radial-gradient(circle at 68% 34%, ${c} 0 19%, transparent 19.5%), repeating-linear-gradient(0deg, ${b}55 0 1px, transparent 1px 12.5%), repeating-linear-gradient(90deg, ${b}55 0 1px, transparent 1px 12.5%), ${a}`;
      case "rings":  return `radial-gradient(circle, ${c} 0 9%, transparent 9.5%), repeating-radial-gradient(circle, ${b} 0 2.5%, ${a} 2.5% 6%)`;
      case "wave":   return `radial-gradient(ellipse 80% 45% at 30% 100%, ${c} 0 98%, transparent 100%), radial-gradient(ellipse 90% 55% at 80% 100%, ${b} 0 98%, transparent 100%), ${a}`;
      case "split":  return `radial-gradient(circle at 36% 64%, ${c} 0 15%, transparent 15.5%), linear-gradient(135deg, ${a} 0 50%, ${b} 50%)`;
      case "dots":   return `radial-gradient(circle, ${b} 0 20%, transparent 22%) 0 0/20% 20%, linear-gradient(${a} 0 70%, ${c} 70%)`;
      case "aurora": return `linear-gradient(165deg, ${a} 0 25%, ${b} 52%, ${c} 64%, ${a} 92%)`;
    }
  }
  function accent(al) {
    const sat = (h) => { const [r, g, b] = [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16)); return Math.max(r, g, b) - Math.min(r, g, b); };
    return `color-mix(in oklab, ${[...al.pal].sort((x, y) => sat(y) - sat(x))[0]} 80%, white)`;
  }

  let ai = +(qs.get("album") ?? 2) % albums.length;
  function load() {
    const al = albums[ai];
    vinyl.innerHTML = `<div class="disc"><div class="label"><div class="cover" style="background:${coverBg(al)}"></div></div><div class="hole"></div></div><div class="sheen"></div>`;
    root.style.setProperty("--accent", accent(al));
  }

  // spin-up / spin-down like a real platter; .disc and anything marked data-spin turns with the record
  let spinning = false, playing = false, busy = false, angle = 0, vel = 0, last = performance.now();
  (function tick(now) {
    const dt = Math.min(0.05, (now - last) / 1000); last = now;
    const target = spinning ? 33.333 * 6 : 0;
    vel += (target - vel) * Math.min(1, dt * (target > vel ? 1.6 : 2.4));
    angle = (angle + vel * dt) % 360;
    for (const el of deck.querySelectorAll(".disc, [data-spin]")) el.style.transform = `rotate(${angle}deg)`;
    requestAnimationFrame(tick);
  })(last);

  const sync = () => { playBtn.textContent = playing ? "■ Stop" : "▶ Play"; };
  async function play() {
    spinning = true; deck.classList.add("spinning");
    await wait(40);
    deck.classList.add("arm-on"); await wait(950);
    deck.classList.add("arm-down"); await wait(450);
    playing = true;
  }
  async function stop() {
    playing = false;
    deck.classList.remove("arm-down"); await wait(450);
    deck.classList.remove("arm-on"); await wait(300);
    spinning = false; deck.classList.remove("spinning");
  }
  const run = (fn) => async () => { if (busy) return; busy = true; await fn(); busy = false; sync(); };
  const toggle = run(() => (playing ? stop() : play()));
  const nextRecord = run(async () => {
    const was = playing;
    if (playing) await stop();
    while (vel > 2) await wait(50);
    deck.classList.add("lifted"); await wait(450);
    ai = (ai + 1) % albums.length; load();
    deck.classList.remove("lifted"); await wait(500);
    if (was || !qs.has("embed")) await play();
  });

  deck.onclick = toggle;
  playBtn.onclick = toggle;
  document.querySelector("#next").onclick = nextRecord;
  document.querySelector("#light").onclick = () => root.classList.toggle("light");
  addEventListener("keydown", (e) => { if (e.key === " ") { e.preventDefault(); toggle(); } });
  load(); sync();
  setTimeout(toggle, 500);
})();

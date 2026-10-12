(() => {
  const $ = (s, r = document) => r.querySelector(s);
  const albums = window.ALBUMS;
  const state = {
    page: "albums", view: "grid", group: "genre", tgroup: "album", sort: "artist", q: "", pl: 0,
    album: null, track: 0, playing: false, spinning: false, pos: 0, motion: "smooth", rpm: 33.333,
    crates: {}, crateKey: null, queue: [], playlists: [],
    draft: [], radio: null, recent: [],   // radio: the station being composed, the one on air, recent stations
  };
  // App setting (not taken from the OS): smooth = full timings, fast = half, none = instant (the record still spins).
  const M = () => ({ smooth: 1, fast: 0.5, none: 0 })[state.motion];
  const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

  // ---------- Cover art ----------
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
  const ink = (al) => al.pal[al.pat === "aurora" ? 2 : al.pat === "sun" || al.pat === "dots" ? 0 : 2];
  function coverEl(al, cls = "", withText = true) {
    const d = document.createElement("div");
    d.className = "cover " + cls;
    d.style.background = coverBg(al);
    if (withText) d.innerHTML = `<div class="ct" style="color:${ink(al)}">${al.title}<span class="ca">${al.artist}</span></div>`;
    return d;
  }
  const paintCover = (el, al, withText = true) => { const c = coverEl(al, el.className.replace("cover", ""), withText); el.style.background = c.style.background; el.innerHTML = c.innerHTML; };
  const thumb = (al) => `<div class="cover" style="background:${coverBg(al)}"></div>`;

  // ---------- Vinyl ----------
  const discs = new Set();
  function fillVinyl(v, al) {
    v.innerHTML = `<div class="disc"><div class="label"></div><div class="hole"></div></div><div class="sheen"></div>`;
    $(".label", v).append(coverEl(al, "", false));
    const disc = $(".disc", v);
    disc.dataset.disc = albums.indexOf(al);
    discs.add(disc);
    return v;
  }
  function vinylEl(al) { const v = document.createElement("div"); v.className = "vinyl"; return fillVinyl(v, al); }

  // One clock for every visible disc of the playing album; spin-up / spin-down like a real platter.
  let angle = 0, vel = 0, last = performance.now();
  function tick(now) {
    const dt = Math.min(0.05, (now - last) / 1000); last = now;
    const m = M();
    // the spin is the "music is playing" cue, so it runs in every motion mode; None only skips the spin-up/down ramp
    const target = state.spinning ? state.rpm * 6 : 0;                  // deg/s
    vel += (target - vel) * Math.min(1, dt * (target > vel ? 1.6 : 2.4) / Math.max(m, 0.001));
    angle = (angle + vel * dt) % 360;
    const cur = String(albums.indexOf(state.album));
    for (const d of discs) {
      if (!d.isConnected) { discs.delete(d); continue; }
      d.style.transform = d.dataset.disc === cur ? `rotate(${angle}deg)` : "";
    }
    if (state.playing && state.album) {
      state.pos += dt;
      const t = state.album.tracks[state.track];
      if (state.pos >= t.dur) nextTrack(1);
      renderProgress();
    }
    drawVis(dt);
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);

  // ---------- Library: filter, sort, group ----------
  const isHiRes = (al) => /^24|DSD|1-bit/.test(al.res) || al.fmt.startsWith("DSD");
  const isAlbumPage = () => state.page === "albums";
  const albumGroupMode = () => state.group;
  const albumItems = (a) => albums[a].tracks.map((_, t) => ({ a, t }));
  const matches = (s) => s.toLowerCase().includes(state.q.trim().toLowerCase());
  function cmpAlbums(x, y) {
    if (state.sort === "year") return x.year - y.year || x.title.localeCompare(y.title);
    if (state.sort === "title") return x.title.localeCompare(y.title);
    return x.artist.localeCompare(y.artist) || x.year - y.year;
  }
  // keeps list order inside each group; group names A–Z unless the keys already come in order
  function groupBy(list, keyOf, sortKeys = true) {
    const m = new Map();
    for (const x of list) { const k = keyOf(x); if (!m.has(k)) m.set(k, []); m.get(k).push(x); }
    const groups = [...m].map(([key, list]) => ({ key, list }));
    return sortKeys ? groups.sort((a, b) => a.key.localeCompare(b.key)) : groups;
  }
  function albumGroups() {
    const list = albums.filter((al) => matches(`${al.title} ${al.artist} ${al.genre} ${al.year}`)).sort(cmpAlbums);
    const g = albumGroupMode();
    return g === "none" ? [{ key: "", list }] : groupBy(list, (al) => al[g]);
  }
  function trackItems(sortTitles) {
    const out = [];
    for (const al of albums.slice().sort(cmpAlbums)) {
      const a = albums.indexOf(al), hit = matches(`${al.title} ${al.artist} ${al.genre}`);
      al.tracks.forEach((tr, t) => (hit || matches(tr.title)) && out.push({ a, t }));
    }
    const title = ({ a, t }) => albums[a].tracks[t].title;
    if (sortTitles && state.sort === "title") out.sort((x, y) => title(x).localeCompare(title(y)));
    return out;
  }

  // ---------- Views ----------
  const view = $("#view");
  // artist and genre groups can start a radio station of their own
  const groupHead = (key, n, unit = "", kind = "") => `<h3 class="group-h"><span>${key} · ${n}${unit}</span>${kind ? radioBtn(kind, key) : ""}</h3>`;

  function renderGrid(groups) {
    const frag = document.createDocumentFragment();
    for (const { key, list } of groups) {
      const sec = document.createElement("section"); sec.className = "group";
      if (key) sec.innerHTML = groupHead(key, list.length, "", albumGroupMode());
      const g = document.createElement("div"); g.className = "grid";
      for (const al of list) {
        const c = document.createElement("div");
        c.className = "card" + (al === state.album ? " playing" : "");
        c.dataset.album = albums.indexOf(al);
        c.draggable = true;
        const art = document.createElement("div"); art.className = "art";
        art.append(vinylEl(al), coverEl(al));
        const fab = document.createElement("div"); fab.className = "playfab";
        fab.textContent = al === state.album && state.playing ? "❚❚" : "▶";
        art.append(fab);
        c.append(art);
        c.insertAdjacentHTML("beforeend", `<div class="t">${al.title}</div><div class="a">${al.artist} · ${al.year} ${isHiRes(al) ? `<span class="hires">HI-RES</span>` : ""}</div>`);
        c.onclick = () => playOnTurntable(al, c);
        fab.onclick = (e) => { e.stopPropagation(); togglePlay(al); };
        g.append(c);
      }
      sec.append(g);
      frag.append(sec);
    }
    view.replaceChildren(frag);
  }

  // Shelf: a bookcase. Each group fills as many boards as it needs; a board holds as many sleeves as fit
  // with room left for a hovered and the playing sleeve to open at the same time, so nothing reflows on hover.
  const SLEEVE = 170;                                   // .slot --w
  let shelfN = 0;
  function shelfPerRow() {
    const tilt = parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--tilt")) * Math.PI / 180;
    const step = SLEEVE * Math.cos(tilt) + 6;           // .slot --step
    const open = (SLEEVE + 30 - step) + (SLEEVE * 1.55 + 30 - step);
    const avail = view.clientWidth - 56 - 54;           // .view and .shelf-row side padding
    return Math.max(1, Math.floor((avail - open) / step));
  }
  function renderShelf(groups) {
    const n = (shelfN = shelfPerRow());
    const frag = document.createDocumentFragment();
    for (const { key, list } of groups) {
      const s = document.createElement("section"); s.className = "shelf";
      if (key) s.innerHTML = groupHead(key, list.length, "", albumGroupMode());
      for (let i = 0; i < list.length; i += n) {
        const row = document.createElement("div"); row.className = "shelf-row";
        for (const al of list.slice(i, i + n)) {
          const slot = document.createElement("div");
          slot.className = "slot" + (al === state.album ? " playing" : "");
          slot.dataset.album = albums.indexOf(al);
          slot.draggable = true;
          slot.setAttribute("aria-label", `${al.title} — ${al.artist}`);
          const it = document.createElement("div"); it.className = "sleeve3d";
          it.style.setProperty("--edge", al.pal[1]);
          it.append(vinylEl(al), coverEl(al));
          it.insertAdjacentHTML("beforeend", `<div class="edge"></div>`);
          slot.append(it);
          slot.onclick = () => playOnTurntable(al, slot);
          row.append(slot);
        }
        s.append(row);
        s.insertAdjacentHTML("beforeend", `<div class="board"></div>`);
      }
      frag.append(s);
    }
    view.replaceChildren(frag);
  }

  // Crate: one crate when ungrouped, one smaller crate per group otherwise; each flips on its own.
  const crateLists = new Map();
  function renderCrate(groups) {
    const multi = albumGroupMode() !== "none";
    crateLists.clear();
    const wrap = document.createElement("div"); wrap.className = "crate-wrap" + (multi ? " multi" : "");
    const crates = document.createElement("div"); crates.className = "crates";
    for (const { key, list } of groups) {
      const k = key || "all";
      crateLists.set(k, list);
      if (!(state.crates[k] < list.length)) state.crates[k] = Math.max(0, list.indexOf(state.album));
      const c = document.createElement("div"); c.className = "crate"; c.dataset.key = k;
      const label = multi ? `${key} · ${list.length}` : state.sort === "year" ? `${list[0].year} — ${list.at(-1).year}` : "A — Z";
      c.innerHTML = `<div class="crate-stage"></div><div class="crate-box"><span>${label}</span>${multi ? radioBtn(albumGroupMode(), key, false) : ""}</div>`;
      const stage = $(".crate-stage", c);
      list.forEach((al, i) => {
        const r = document.createElement("div"); r.className = "rec"; r.draggable = true;
        r.dataset.album = albums.indexOf(al);
        r.append(coverEl(al));
        r.onclick = () => (i === state.crates[k] ? playOnTurntable(al, r) : setCrate(k, i));
        stage.append(r);
      });
      c.addEventListener("wheel", (e) => crateWheel(e, k), { passive: false });
      crates.append(c);
    }
    const side = document.createElement("div"); side.className = "crate-side";
    wrap.append(crates, side);
    view.replaceChildren(wrap);
    if (!crateLists.has(state.crateKey)) state.crateKey = [...crateLists].find(([, l]) => l.includes(state.album))?.[0] ?? [...crateLists.keys()][0];
    layoutCrate();
  }
  function layoutCrate() {
    for (const c of view.querySelectorAll(".crate")) {
      const key = c.dataset.key, k = parseFloat(getComputedStyle(c).getPropertyValue("--k")), px = (v) => `${v * k}px`;
      c.classList.toggle("active", key === state.crateKey);
      c.querySelectorAll(".rec").forEach((r, i) => {
        const d = i - state.crates[key];
        let t, o = 1;
        if (d < 0) { t = `translateZ(${px(140)}) translateY(${px(40)}) rotateX(-70deg)`; o = 0; }   // flipped forward, out of view
        else if (d === 0) t = `translateZ(${px(30)}) translateY(${px(-40)}) rotateX(-4deg)`;
        else { t = `translateZ(${px(-d * 26)}) translateY(0px) rotateX(${Math.min(18, 8 + d * 2)}deg)`; o = d > 9 ? 0 : 1; }
        r.style.transform = t; r.style.opacity = o; r.style.zIndex = 100 - Math.abs(d) + (d < 0 ? 50 : 0);
        r.style.filter = d > 0 ? `brightness(${Math.max(.45, 1 - d * .07)})` : "";
      });
    }
    const side = $(".crate-side", view), al = crateLists.get(state.crateKey)?.[state.crates[state.crateKey]];
    if (!side || !al) return;
    const a = albums.indexOf(al);
    side.innerHTML = `<div class="np-kicker">${al.genre} · ${al.year}</div><h2>${al.title}</h2><p>${al.artist}</p>
      <div class="np-tech" style="margin:0 0 18px"><span>${al.fmt}</span><span>${al.res}</span><span>${al.tracks.length} tracks</span></div>
      <button class="btn" id="cratePlay">${al === state.album && state.playing ? "❚❚ Pause" : "▶ Play"}</button><button class="btn ghost" data-act="menu" data-album="${a}">＋ Queue</button>`;
    $("#cratePlay").onclick = () => al === state.album && state.playing ? togglePlay(al) : playOnTurntable(al, recOf(a));
  }
  const recOf = (a) => view.querySelector(`.rec[data-album="${a}"]`);
  function setCrate(k, i) {
    state.crates[k] = Math.max(0, Math.min(crateLists.get(k).length - 1, i));
    state.crateKey = k;
    layoutCrate();
  }
  function crateFocus(a) {
    for (const [k, list] of crateLists) { const i = list.indexOf(albums[a]); if (i >= 0) return setCrate(k, i); }
  }
  let wheelAcc = 0, wheelAt = 0, wheelKey = null;
  function crateWheel(e, k) {
    const dir = Math.sign(e.deltaY), i = state.crates[k] + dir;
    if (!dir || i < 0 || i >= crateLists.get(k).length) return;    // past either end the page scrolls on
    e.preventDefault();
    if (wheelKey !== k) { wheelKey = k; wheelAcc = 0; }
    wheelAcc = Math.max(-120, Math.min(120, wheelAcc + e.deltaY));
    const now = performance.now();
    if (Math.abs(wheelAcc) >= 50 && now - wheelAt > 180) { setCrate(k, state.crates[k] + Math.sign(wheelAcc)); wheelAcc = 0; wheelAt = now; }
  }

  // Tracks (and playlists). Columns already named by the group header are left out of the rows.
  function trackRow({ a, t }, mode) {
    const al = albums[a], tr = al.tracks[t];
    const lead = mode === "album" ? `<span class="no">${tr.no}</span>` : `<span class="lead">${thumb(al)}</span>`;
    const title = mode === "album" ? `<span class="tt">${tr.title}</span>` : `<span class="tt">${tr.title}<small>${al.artist}</small></span>`;
    const cols = mode === "none" ? `<span class="c">${al.title}</span><span class="c">${al.genre}</span>` : mode === "album" ? "" : `<span class="c">${al.title}</span>`;
    return `<div class="tr" draggable="true" data-a="${a}" data-t="${t}">${lead}${title}${cols}<span class="d">${fmtT(tr.dur)}</span><button class="add" data-act="menu" title="Add to queue">＋</button></div>`;
  }
  function albumHead(a) {
    const al = albums[a];
    return `<div class="talbum${al === state.album ? " playing" : ""}" draggable="true" data-album="${a}">${thumb(al)}
      <div><h3>${al.title}</h3><div class="sub">${al.artist} · ${al.year} · ${al.fmt} ${al.res}</div></div>
      <div class="acts"><button class="icon-btn" data-act="play" title="Play">▶</button><button class="icon-btn" data-act="menu" title="Add to queue">＋</button></div></div>`;
  }
  function renderTracks() {
    const pl = state.page === "playlist";
    const mode = pl ? "plain" : state.tgroup;
    const items = pl
      ? state.playlists[state.pl].items.filter(({ a, t }) => matches(`${albums[a].tracks[t].title} ${albums[a].title} ${albums[a].artist}`))
      : trackItems(mode !== "album");
    const groups = mode === "album" ? groupBy(items, (x) => x.a, false)
      : mode === "genre" ? groupBy(items, (x) => albums[x.a].genre)
      : [{ key: "", list: items }];
    view.innerHTML = !items.length ? emptyMsg() : groups.map(({ key, list }) =>
      `<section class="group">${mode === "album" ? albumHead(key) : key ? groupHead(key, list.length, " tracks", mode === "genre" ? "genre" : "") : ""}
        <div class="tlist m-${mode}">${list.map((x) => trackRow(x, mode)).join("")}</div></section>`).join("");
    markTracks();
    return items.length;
  }
  function markTracks() {
    const a = albums.indexOf(state.album);
    for (const r of document.querySelectorAll(".tr")) r.classList.toggle("cur", +r.dataset.a === a && +r.dataset.t === state.track);
  }
  const emptyMsg = () => `<p class="empty">${state.q.trim() ? `Nothing matches “${esc(state.q.trim())}”.` : "Nothing here yet."}</p>`;

  const LABEL = { none: "None", artist: "Artist", genre: "Genre", album: "Album", title: "Title", year: "Year" };
  function render() {
    let count = "";
    const radio = state.page === "radio";
    if (radio) renderRadioPage();
    else if (isAlbumPage()) {
      const groups = albumGroups();
      count = groups.reduce((n, g) => n + g.list.length, 0);
      if (!count) view.innerHTML = emptyMsg();
      else ({ grid: renderGrid, shelf: renderShelf, crate: renderCrate })[state.view](groups);
    } else count = renderTracks();
    const ap = isAlbumPage(), pl = state.page === "playlist";
    $("#pageTitle").textContent = pl ? state.playlists[state.pl].name : { albums: "Albums", tracks: "Tracks", radio: "Radio" }[state.page];
    $("#count").textContent = count;
    $("#viewSeg").hidden = !ap;
    $("#plActions").hidden = !pl;
    $("#sortBtn").hidden = pl || radio;
    $("#groupBtn").hidden = pl || radio;
    $("#sortBtn b").textContent = LABEL[state.sort];
    $("#groupBtn b").textContent = LABEL[ap ? state.group : state.tgroup];
  }
  function setPage(page, pl = 0) { state.page = page; state.pl = pl; renderNav(); view.scrollTop = 0; render(); }
  function renderNav(fresh = -1) {
    $("#plNav").innerHTML = state.playlists.map((p, i) =>
      `<a data-page="playlist" data-pl="${i}" class="${state.page === "playlist" && state.pl === i ? "active" : ""}${i === fresh ? " fresh" : ""}">${esc(p.name)}</a>`).join("");
    for (const a of $("#nav").querySelectorAll("[data-page]:not([data-pl])")) a.classList.toggle("active", a.dataset.page === state.page);
  }
  new ResizeObserver(() => { if (isAlbumPage() && state.view === "shelf" && shelfPerRow() !== shelfN) render(); }).observe(view);

  // ---------- Playback (simulated) ----------
  const fmtT = (s) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, "0")}`;
  function togglePlay(al) {
    if (state.album !== al) { endRadio(); state.album = al; state.track = 0; state.pos = 0; state.playing = true; applyAlbum(); }
    else state.playing = !state.playing;
    syncPlaying();
  }
  function playTrack(a, t, play = true) {
    if (busy) return;
    state.track = t; state.pos = 0;
    if (state.album !== albums[a]) { state.album = albums[a]; applyAlbum(); }
    if (play) state.playing = true;
    syncPlaying(); renderNowPlaying(); renderPlayerMeta();
  }
  function playItems(items) {
    if (!items.length || busy) return;
    endRadio();
    const [first, ...rest] = items;
    state.queue = rest.map(({ a, t }) => ({ a, t, id: ++qid }));
    renderQueue();
    playTrack(first.a, first.t);
  }
  // the queue plays before the rest of the current album, or before the radio station's next pick
  function nextTrack(dir) {
    if (!state.album || busy) return;
    const r = state.radio;
    if (dir > 0 && state.queue.length) {
      const it = state.queue.shift(); renderQueue();
      if (r) { r.hist.push(r.cur); r.cur = it; }
      return playTrack(it.a, it.t, state.playing);
    }
    if (r) return dir > 0 ? radioStep() : radioBack();
    const n = state.album.tracks.length;
    state.track = (state.track + dir + n) % n; state.pos = 0;
    renderNowPlaying(); renderPlayerMeta();
  }
  function applyAlbum() {
    const al = state.album;
    document.documentElement.style.setProperty("--accent", pickAccent(al));
    paintCover($("#miniCover"), al, false);
    fillVinyl($("#miniVinyl"), al);
    fillVinyl($("#bigVinyl"), al);
    $("#npBg").style.background = coverBg(al);
    renderNowPlaying(); renderPlayerMeta();
  }
  // most saturated palette colour, lightened if too dark to read
  const rgbOf = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16));
  const accentBase = (al) => {
    const sat = (h) => { const c = rgbOf(h); return Math.max(...c) - Math.min(...c); };
    return [...al.pal].sort((x, y) => sat(y) - sat(x))[0];
  };
  const pickAccent = (al) => `color-mix(in oklab, ${accentBase(al)} 80%, white)`;
  function syncPlaying() {
    document.body.classList.toggle("playing-state", state.playing);
    $("#play").textContent = state.playing ? "❚❚" : "▶";
    if (!busy) {   // transport controls act at once: needle and platter follow play/pause
      state.spinning = state.playing;
      if (state.playing) $("#deck").classList.add("arm-on");
      $("#deck").classList.toggle("arm-down", state.playing);
    }
    $("#deck").classList.toggle("spinning", state.spinning);
    markTracks();
    if (isAlbumPage() && state.view === "crate") return layoutCrate();
    // toggle classes in place so the vinyl slide-out animates
    const cur = String(albums.indexOf(state.album));
    for (const el of view.querySelectorAll("[data-album]")) {
      el.classList.toggle("playing", el.dataset.album === cur);
      const fab = $(".playfab", el);
      if (fab) fab.textContent = el.dataset.album === cur && state.playing ? "❚❚" : "▶";
    }
  }
  function renderPlayerMeta() {
    const al = state.album, t = al.tracks[state.track];
    $("#pTitle").textContent = t.title;
    $("#pArtist").textContent = `${al.artist} · ${al.title}`;
    $("#pBadge").innerHTML = techLabel(al, t);
    $("#pHra").toggleAttribute("hidden", !isHiRes(al));   // an svg has no .hidden property
    $("#tDur").textContent = fmtT(t.dur);
    renderProgress();
    markTracks();
  }
  // "FLAC 24 bit, 192 kHz, 4813k, Stereo"; lossy files have no bit depth: "MP3 44.1 kHz, 320k, Stereo"
  function techLabel(al, t) {
    const [bits, rate] = al.res.split("/"), lossy = al.res.endsWith("k");
    const parts = lossy ? [`${al.khz} kHz`] : [`${parseInt(bits)} bit`, rate.endsWith("M") ? `${rate.slice(0, -1)} MHz` : `${rate} kHz`];
    return `<b>${al.fmt}</b> ${[...parts, `${t.kbps}k`, al.ch === 1 ? "Mono" : "Stereo"].join(", ")}`;
  }
  // ---------- Position ----------
  // Drag the bar (Shift: 10x finer) and the track seeks on release; its first few pixels are 0:00. Arrow keys on the
  // focused bar step 5 s (PgUp/PgDn 30 s, Home: the start), Ctrl+Left/Right anywhere. The elapsed time is a text box
  // that takes the CLI's seek: 90, 1:30, 1:02:03, or relative +10, -10, +0:30.
  const tCur = $("#tCur"), pbar = $("#bar"), SNAP = 6;
  let scrub = null, scrubX = 0, tEdited = false;   // scrub: the position shown while dragging
  const curDur = () => state.album.tracks[state.track].dur;
  function renderProgress() {
    const dur = curDur(), pos = scrub ?? state.pos;
    if (document.activeElement !== tCur) tCur.value = fmtT(pos);
    $("#barFill").style.width = `${(pos / dur) * 100}%`;
    pbar.setAttribute("aria-valuetext", `${fmtT(pos)} of ${fmtT(dur)}`);
    const back = state.pos > 3 ? "Back to the start" : "Previous track";   // what ⏮ does now
    if ($("#prev").title !== back) $("#prev").title = back;
  }
  function seekTo(s) {
    if (!state.album) return;
    state.pos = Math.min(curDur(), Math.max(0, s));
    renderProgress();
  }
  const showTip = (f) => { $("#barTip").textContent = fmtT(f * curDur()); $("#barTip").style.left = `${f * 100}%`; };
  const fracAt = (x) => { const r = pbar.getBoundingClientRect(); return x - r.left < SNAP ? 0 : Math.min(1, (x - r.left) / r.width); };
  pbar.onpointerdown = (e) => {
    if (!state.album) return;
    pbar.setPointerCapture(e.pointerId); pbar.classList.add("drag");
    scrubX = e.clientX;
    scrub = e.shiftKey ? state.pos : fracAt(e.clientX) * curDur();   // Shift: fine-tune from where it is
    renderProgress();
  };
  pbar.onpointermove = (e) => {
    if (!state.album) return;
    if (!pbar.hasPointerCapture(e.pointerId)) return showTip(fracAt(e.clientX));   // hover: the time under the pointer
    const r = pbar.getBoundingClientRect();
    scrub = !e.shiftKey && e.clientX - r.left < SNAP ? 0
      : Math.min(curDur(), Math.max(0, scrub + ((e.clientX - scrubX) / r.width) * curDur() * (e.shiftKey ? 0.1 : 1)));
    scrubX = e.clientX;
    renderProgress();
  };
  pbar.onpointerup = (e) => { if (scrub != null) { const s = scrub; scrub = null; seekTo(s); } pbar.classList.remove("drag"); showTip(fracAt(e.clientX)); };
  pbar.onpointercancel = () => { scrub = null; pbar.classList.remove("drag"); renderProgress(); };
  const POS_KEYS = { ArrowRight: 5, ArrowUp: 5, ArrowLeft: -5, ArrowDown: -5, PageUp: 30, PageDown: -30 };
  pbar.onkeydown = (e) => {
    if (!state.album || (!(e.key in POS_KEYS) && e.key !== "Home")) return;
    e.preventDefault(); e.stopPropagation();   // the arrows flip the crate elsewhere
    seekTo(e.key === "Home" ? 0 : state.pos + POS_KEYS[e.key]);
  };
  function parseTime(text) {
    const m = text.trim().replace("−", "-").match(/^([+-]?)\s*(\d+(?::[0-5]?\d){0,2})$/);
    if (!m) return null;
    const s = m[2].split(":").reduce((acc, x) => acc * 60 + +x, 0);
    if (m[1]) return state.pos + (m[1] === "-" ? -s : s);   // relative: clamped, as in the CLI
    return s <= curDur() ? s : null;                        // past the end is refused, as in the CLI
  }
  tCur.onfocus = () => { tEdited = false; tCur.select(); };
  tCur.oninput = () => { tEdited = true; tCur.classList.remove("bad"); };
  tCur.onkeydown = (e) => {
    e.stopPropagation();
    if (e.key === "Enter") {
      const s = parseTime(tCur.value);
      if (s == null) { tCur.classList.add("bad"); return tCur.select(); }
      seekTo(s); tEdited = false; tCur.blur();
    } else if (e.key === "Escape") { tEdited = false; tCur.blur(); }
  };
  tCur.onblur = () => {   // the box stops the clock while you type; clicking away keeps an edited, valid time
    const s = tEdited ? parseTime(tCur.value) : null;
    if (s != null) seekTo(s);
    tEdited = false; tCur.classList.remove("bad"); renderProgress();
  };
  function renderNowPlaying() {
    const al = state.album, a = albums.indexOf(al);
    $("#npAlbum").textContent = al.title;
    $("#npArtist").textContent = `${al.artist} · ${al.year}`;
    $("#npTracks").innerHTML = al.tracks.map((t, i) =>
      `<li class="${i === state.track ? "cur" : ""}" draggable="true" data-a="${a}" data-t="${i}"><span class="n">${i === state.track && state.playing ? "♪" : t.no}</span><span>${t.title}</span><span class="d">${fmtT(t.dur)}</span></li>`).join("");
  }
  $("#npTracks").onclick = (e) => {
    const li = e.target.closest("li"); if (!li) return;
    endRadio();
    state.track = +li.dataset.t; state.pos = 0; state.playing = true; syncPlaying(); renderNowPlaying(); renderPlayerMeta();
  };

  // ---------- Player bar: volume, spectrum analyzer ----------
  // Volume in percent, to 0.1; 100% is the player's bit-perfect path (the samples are left untouched).
  // Typed values are exact, the bar moves in whole percents: drag (Shift: 10x finer), scroll (1%, Shift 5%),
  // arrows / PgUp / PgDn / Home / End on the focused bar, Ctrl+Up/Down anywhere.
  let vol = 80, muted = false;
  const volBar = $("#volBar"), volNum = $("#volNum");
  const fmtVol = (v) => `${+v.toFixed(1)}%`;
  function renderVol() {
    $("#volFill").style.width = `${vol}%`;
    $("#vol").classList.toggle("muted", muted || vol === 0);
    $("#vol").classList.toggle("low", vol < 50);
    $("#volIco").title = muted ? "Unmute" : "Mute";
    volBar.setAttribute("aria-valuenow", vol);
    volBar.setAttribute("aria-valuetext", muted ? "Muted" : fmtVol(vol));
    if (document.activeElement !== volNum) volNum.value = fmtVol(vol);
    volNum.title = vol === 100 ? "Bit-perfect: the samples are left untouched" : "Type a volume: 37, 37.5, +5, -10%";
  }
  const setVol = (v, step = 0.1) => { vol = +(Math.round(Math.min(100, Math.max(0, v)) / step) * step).toFixed(1); muted = false; renderVol(); };
  // one step up or down to the next whole multiple of s: 37.5 -> 38 / 37, and with s = 5: 37 -> 40 / 35
  const stepVol = (dir, s) => setVol(dir > 0 ? Math.floor(vol / s + 1e-9) * s + s : Math.ceil(vol / s - 1e-9) * s - s);
  // "37", "37%", "37.5" absolute; "+5", "-10%" relative (clamped, as in the CLI); "0.37", "+0.1": the CLI's fractions
  function parseVol(text) {
    const m = text.trim().replace(",", ".").replace("−", "-").match(/^([+-]?)\s*(\d*\.?\d+)\s*(%?)$/);
    if (!m) return null;
    const n = !m[3] && m[2].includes(".") && +m[2] <= 1 ? +m[2] * 100 : +m[2];
    if (m[1]) return vol + (m[1] === "-" ? -n : n);
    return n <= 100 ? n : null;   // past 100% is refused, as in the CLI
  }
  $("#volIco").onclick = () => { muted = !muted; if (!muted && vol === 0) vol = 50; renderVol(); };

  let dragX = 0, dragV = 0;
  volBar.onpointerdown = (e) => {
    volBar.setPointerCapture(e.pointerId); volBar.classList.add("drag");
    dragX = e.clientX;
    if (e.shiftKey) { dragV = vol; return; }   // Shift: fine-tune from where it is, no jump to the pointer
    const r = volBar.getBoundingClientRect();
    setVol((dragV = ((e.clientX - r.left) / r.width) * 100), 1);
  };
  volBar.onpointermove = (e) => {
    if (!volBar.hasPointerCapture(e.pointerId)) return;
    dragV = Math.min(100, Math.max(0, dragV + ((e.clientX - dragX) / volBar.offsetWidth) * 100 * (e.shiftKey ? 0.1 : 1)));
    dragX = e.clientX;
    setVol(dragV, 1);
  };
  volBar.onpointerup = volBar.onpointercancel = () => volBar.classList.remove("drag");
  const VOL_KEYS = { ArrowRight: 1, ArrowUp: 1, ArrowLeft: -1, ArrowDown: -1, PageUp: 10, PageDown: -10 };
  volBar.onkeydown = (e) => {
    const d = VOL_KEYS[e.key];
    if (d == null && e.key !== "Home" && e.key !== "End") return;
    e.preventDefault(); e.stopPropagation();   // the arrows flip the crate elsewhere
    d ? stepVol(d, Math.abs(d)) : setVol(e.key === "End" ? 100 : 0);
  };
  // Shift+wheel arrives as a horizontal scroll on Windows
  $("#vol").onwheel = (e) => { e.preventDefault(); const d = e.deltaY || e.deltaX; if (d) stepVol(-d, e.shiftKey ? 5 : 1); };

  volNum.onfocus = () => volNum.select();
  volNum.oninput = () => volNum.classList.remove("bad");
  volNum.onkeydown = (e) => {
    e.stopPropagation();
    if (e.key === "Enter") {
      const v = parseVol(volNum.value);
      if (v == null) { volNum.classList.add("bad"); return volNum.select(); }
      setVol(v); volNum.blur();
    } else if (e.key === "Escape") { volNum.value = fmtVol(vol); volNum.blur(); }
    else if (e.key === "ArrowUp" || e.key === "ArrowDown") {
      e.preventDefault(); stepVol(e.key === "ArrowUp" ? 1 : -1, e.shiftKey ? 10 : 1);
      volNum.value = fmtVol(vol); volNum.select();
    }
  };
  volNum.onblur = () => {   // clicking away keeps a valid value, drops an invalid one
    const v = parseVol(volNum.value);
    if (v != null && v !== vol) setVol(v);
    volNum.value = fmtVol(vol); volNum.classList.remove("bad");
  };

  // After Winamp's analyzer: 16 bars of 16 rows that jump up and sink back, peak dots that hang, then drop, in the album's
  // colour. The mockup fakes the audio: a kick on the beat in the low bands, hats off the beat in the high ones, drifting
  // mids. It runs in every motion mode, like the record: it shows that music is playing.
  const vis = $("#vis"), vctx = vis.getContext("2d");
  const BARS = 16, ROWS = 16, BW = 4, VW = BARS * (BW + 1) - 1, VH = 32, RH = VH / ROWS;
  const lvl = new Float32Array(BARS), bars = new Float32Array(BARS), peaks = new Float32Array(BARS), hold = new Float32Array(BARS), drop = new Float32Array(BARS);
  let visAcc = 0, visIdle = false;
  vis.width = VW * devicePixelRatio; vis.height = VH * devicePixelRatio;
  vctx.scale(devicePixelRatio, devicePixelRatio);
  function sample(i, t) {
    const f = i / (BARS - 1), a = albums.indexOf(state.album);
    const beat = (t * (84 + ((a * 13 + state.track * 7) % 56))) / 60 % 1;
    const kick = Math.exp(-beat * 7) * Math.max(0, 1 - f * 3.2);
    const hat = Math.exp(-((beat + 0.5) % 1) * 14) * f * f;
    const drift = 0.5 + 0.25 * Math.sin(t * (2.1 + i * 0.37) + i * 1.7) + 0.25 * Math.sin(t * (5.3 + i * 0.23) + a + i * 0.9);
    const section = 0.75 + 0.25 * Math.sin(t * 0.21 + a);
    const body = (0.78 - 0.5 * f) * drift * drift * section * (0.55 + Math.random() * 0.9);
    return Math.max(0, Math.min(1, body + kick * 0.6 + hat * 0.55));
  }
  function drawVis(dt) {
    const on = state.playing && !!state.album;
    if (!on && visIdle) return;
    if ((visAcc += dt) >= 1 / 30) {   // new spectrum 30 times a second, the falls run every frame
      visAcc %= 1 / 30;
      for (let i = 0; i < BARS; i++) lvl[i] = on ? sample(i, state.pos) : 0;
    }
    let any = false;
    for (let i = 0; i < BARS; i++) {
      bars[i] = Math.max(lvl[i], bars[i] - 2.4 * dt);
      if (bars[i] >= peaks[i]) { peaks[i] = bars[i]; hold[i] = 0.3; drop[i] = 0; }
      else if ((hold[i] -= dt) < 0) peaks[i] = Math.max(0, peaks[i] - (drop[i] += 3 * dt) * dt);
      any ||= peaks[i] > 0;
    }
    visIdle = !on && !any;
    if (!state.album) return;
    // the accent (80% with white); darker on the light theme, like --accent-ink
    const root = document.documentElement, dark = root.classList.contains("light") ? 0.62 : 1;
    const [r, g, b] = rgbOf(accentBase(state.album)).map((c) => Math.round((c * 0.8 + 51) * dark));
    const peak = getComputedStyle(root).getPropertyValue("--fg-2");
    vctx.clearRect(0, 0, VW, VH);
    for (let i = 0; i < BARS; i++) {
      const x = i * (BW + 1), h = Math.round(bars[i] * ROWS), p = Math.round(peaks[i] * ROWS);
      for (let row = 0; row < h; row++) {
        vctx.fillStyle = `rgba(${r},${g},${b},${0.4 + (0.6 * row) / (ROWS - 1)})`;
        vctx.fillRect(x, VH - (row + 1) * RH, BW, RH);
      }
      if (p > 0) { vctx.fillStyle = peak; vctx.fillRect(x, VH - p * RH, BW, RH); }
    }
  }

  // ---------- Queue ----------
  let qid = 0;
  const qEl = $("#queue");
  function enqueue(items, at = state.queue.length) {
    const added = items.map(({ a, t }) => ({ a, t, id: ++qid }));
    state.queue.splice(at, 0, ...added);
    renderQueue(added);
    if (!qEl.classList.contains("open"))   // drawer closed: the count badge is the only feedback, so it bumps
      $("#qBtn").animate([{ transform: "scale(1)" }, { transform: "scale(1.3)" }, { transform: "scale(1)" }], { duration: 420 * M(), easing: "ease-out" });
  }
  function renderQueue(fresh = []) {
    const q = state.queue, ids = new Set(fresh.map((x) => x.id));
    $("#qItems").replaceChildren(...q.map((x) => {
      const al = albums[x.a], tr = al.tracks[x.t];
      const r = document.createElement("div");
      r.className = "qi" + (ids.has(x.id) ? " fresh" : "");
      r.draggable = true; r.dataset.id = x.id;
      r.innerHTML = `${thumb(al)}<span class="tt">${tr.title}<small>${al.artist}</small></span><span class="d">${fmtT(tr.dur)}</span><button class="qx" title="Remove">✕</button>`;
      return r;
    }));
    const secs = q.reduce((s, x) => s + albums[x.a].tracks[x.t].dur, 0);
    $("#qSum").textContent = q.length ? `${q.length} track${q.length > 1 ? "s" : ""} · ${Math.round(secs / 60)} min` : "";
    $("#qCount").textContent = q.length || "";
    qEl.classList.toggle("empty", !q.length);
    if (!q.length) closeSave();
    // bring the first added row into view (scrollTop only: scrollIntoView would also scroll the window)
    const f = $("#qItems .fresh"), sc = $("#qScroll");
    if (f && (f.offsetTop < sc.scrollTop || f.offsetTop + f.offsetHeight > sc.scrollTop + sc.clientHeight)) sc.scrollTop = f.offsetTop - 8;
  }
  $("#qItems").onclick = (e) => {
    const r = e.target.closest(".qi"); if (!r) return;
    const i = state.queue.findIndex((x) => x.id === +r.dataset.id);
    if (e.target.closest(".qx")) { state.queue.splice(i, 1); return renderQueue(); }
    const it = state.queue.splice(0, i + 1).at(-1);   // skip to it: rows above it are dropped
    renderQueue(); playTrack(it.a, it.t);
  };
  $("#qClear").onclick = () => { state.queue = []; renderQueue(); };
  let autoDrawer = false, autoClose = 0;
  function setDrawer(open, auto = false) {
    clearTimeout(autoClose);
    autoDrawer = open && auto;
    qEl.classList.toggle("open", open);
    $("#qBtn").classList.toggle("on", open);
    if (!open) closeSave();
  }
  $("#qBtn").onclick = () => setDrawer(!qEl.classList.contains("open"));
  $("#qClose").onclick = () => setDrawer(false);

  // Save as playlist
  // Play: the top row starts now, the rest follow
  $("#qPlay").onclick = () => { const it = state.queue.shift(); renderQueue(); playTrack(it.a, it.t); };

  function closeSave() { $("#qActs").hidden = false; $("#qForm").hidden = true; }
  $("#qSave").onclick = () => { $("#qActs").hidden = true; $("#qForm").hidden = false; const n = $("#qName"); n.value = "New playlist"; n.focus(); n.select(); };
  $("#qCancel").onclick = closeSave;
  $("#qName").onkeydown = (e) => { if (e.key === "Escape") { e.stopPropagation(); closeSave(); } };
  $("#qForm").onsubmit = (e) => {
    e.preventDefault();
    state.playlists.push({ name: $("#qName").value.trim() || "New playlist", items: state.queue.map(({ a, t }) => ({ a, t })) });
    closeSave();
    renderNav(state.playlists.length - 1);
  };

  // Drag and drop: albums and tracks into the queue (at the drop line), queue rows to reorder.
  let drag = null;   // { items } from the library, or { id } for a queue row
  const ghost = document.createElement("div"); ghost.className = "drag-ghost"; document.body.append(ghost);
  const itemsOf = (el) => (el.dataset.t != null ? [{ a: +el.dataset.a, t: +el.dataset.t }] : albumItems(+el.dataset.album));
  document.addEventListener("dragstart", (e) => {
    const el = e.target.closest?.("[draggable=true]");
    if (!el) return;
    closeMenu();
    if (el.classList.contains("qi")) {
      drag = { id: +el.dataset.id };
      e.dataTransfer.effectAllowed = "move";
      e.dataTransfer.setData("application/x-musicspin", "queue");
      requestAnimationFrame(() => el.classList.add("dragging"));
      return;
    }
    const items = itemsOf(el), al = albums[items[0].a], tr = al.tracks[items[0].t];
    drag = { items };
    ghost.replaceChildren(coverEl(al, "", false));
    ghost.insertAdjacentHTML("beforeend", items.length > 1 ? `<span>${al.title}<small>${items.length} tracks</small></span>` : `<span>${tr.title}<small>${al.artist}</small></span>`);
    e.dataTransfer.effectAllowed = "copy";
    e.dataTransfer.setData("application/x-musicspin", "items");   // custom type: text fields won't take the drop
    e.dataTransfer.setDragImage(ghost, 24, 24);
    if (!qEl.classList.contains("open")) setTimeout(() => setDrawer(true, true));   // not inside dragstart: DOM changes there can cancel the drag
  });
  function dropAt(y) {   // insertion index; outside the list (header, footer) means the end
    const rows = [...$("#qItems").children], sc = $("#qScroll").getBoundingClientRect();
    if (y < sc.top || y > sc.bottom) return rows.length;
    const i = rows.findIndex((r) => { const b = r.getBoundingClientRect(); return y < b.top + b.height / 2; });
    return i < 0 ? rows.length : i;
  }
  qEl.addEventListener("dragover", (e) => {
    if (!drag) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = drag.id ? "move" : "copy";
    qEl.classList.add("over");
    const rows = $("#qItems").children, i = dropAt(e.clientY), lastRow = rows[rows.length - 1];
    $("#qLine").style.top = (i < rows.length ? rows[i].offsetTop : lastRow ? lastRow.offsetTop + lastRow.offsetHeight : 0) - 1 + "px";
  });
  qEl.addEventListener("dragleave", (e) => { if (!qEl.contains(e.relatedTarget)) qEl.classList.remove("over"); });
  qEl.addEventListener("drop", (e) => {
    e.preventDefault();
    qEl.classList.remove("over");
    if (!drag) return;
    const i = dropAt(e.clientY);
    if (drag.id) {
      const from = state.queue.findIndex((x) => x.id === drag.id), [it] = state.queue.splice(from, 1);
      state.queue.splice(i > from ? i - 1 : i, 0, it);
      renderQueue();
    } else { enqueue(drag.items, i); drag.dropped = true; }
  });
  // a drawer that opened for the drag closes again: at once if nothing was dropped, else once the pointer leaves it
  document.addEventListener("dragend", () => {
    qEl.classList.remove("over");
    $("#qItems .dragging")?.classList.remove("dragging");
    if (autoDrawer) autoClose = setTimeout(function close() { qEl.matches(":hover") ? (autoClose = setTimeout(close, 400)) : setDrawer(false); }, drag?.dropped ? 1400 : 0);
    drag = null;
  });

  // ---------- Radio (the CLI's `music-spin radio`) ----------
  // A station is a list of tags {k, v, x}: k is the filter kind, x marks a "leave out" (--exclude-*).
  // Values of one kind are ORed, different kinds are ANDed, and a track matching any leave-out is left out.
  const RADIO_ICO = `<svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="2.2"/><path class="w1" d="M8.5 8.5a5 5 0 0 0 0 7M15.5 8.5a5 5 0 0 1 0 7"/><path class="w2" d="M5.6 5.6a9 9 0 0 0 0 12.8M18.4 5.6a9 9 0 0 1 0 12.8"/></svg>`;
  const KINDS = ["genre", "years", "format", "hires", "artist", "album", "search"];   // the order a description reads in
  const fmtId = (al) => (al.fmt.startsWith("DSD") ? "dsd" : al.fmt.toLowerCase());  // DSD64 -> dsd, like the CLI's format ids
  const decade = (y) => `${y - (y % 10)}s`;
  const copyTags = (tags) => tags.map(({ k, v, x }) => ({ k, v: v ?? "", x: !!x }));
  const tagId = (g) => `${g.k}|${g.v}|${g.x ? 1 : 0}`;
  const sameTags = (p, q) => p.length === q.length && p.map(tagId).sort().join() === q.map(tagId).sort().join();
  const onAir = (tags) => !!state.radio && sameTags(state.radio.tags, copyTags(tags));
  const hm = (s) => (s >= 3600 ? `${Math.floor(s / 3600)} h ${Math.round((s % 3600) / 60)} min` : `${Math.round(s / 60)} min`);
  function tagLabel({ k, v }) {
    if (k === "album") return albums[+v].title;
    if (k === "format") return v === "opus" ? "Opus" : v.toUpperCase();
    if (k === "hires") return "Hi-Res";
    if (k === "search") return `“${v}”`;
    return v;
  }
  function tagHit({ k, v }, a, t) {
    const al = albums[a];
    switch (k) {
      case "artist": return al.artist === v;
      case "album":  return String(a) === v;
      case "genre":  return al.genre === v;
      case "years":  return decade(al.year) === v;
      case "format": return fmtId(al) === v;
      case "hires":  return isHiRes(al);
      case "search": { const s = `${al.tracks[t].title} ${al.title} ${al.artist} ${al.genre}`.toLowerCase(); return v.toLowerCase().split(/\s+/).every((w) => s.includes(w)); }
    }
  }
  function stationPool(tags) {
    const inc = groupBy(tags.filter((g) => !g.x), (g) => g.k, false), exc = tags.filter((g) => g.x), out = [];
    albums.forEach((al, a) => al.tracks.forEach((_, t) => {
      if (inc.every(({ list }) => list.some((g) => tagHit(g, a, t))) && !exc.some((g) => tagHit(g, a, t))) out.push({ a, t });
    }));
    return out;
  }
  // "Jazz or Ambient · 1960s · not Kodo Arai"
  function describe(tags, html = false) {
    const b = (g) => (html ? `<b>${esc(tagLabel(g))}</b>` : tagLabel(g));
    const inc = KINDS.map((k) => tags.filter((g) => g.k === k && !g.x)).filter((l) => l.length).map((l) => l.map(b).join(" or "));
    const exc = tags.filter((g) => g.x);
    return [...(inc.length ? inc : ["Whole library"]), ...(exc.length ? [`not ${exc.map(b).join(", ")}`] : [])].join(" · ");
  }
  function radioBtn(k, v, label = true) {
    const on = onAir([{ k, v }]);
    return `<button class="radio-btn${on ? " on" : ""}" data-radio="${k}" data-key="${esc(v)}" title="${esc(v)} radio">${RADIO_ICO}${label ? `<span>${on ? "On air" : "Radio"}</span>` : ""}</button>`;
  }

  // Picks like the player: no repeat within the last 100 picks (pool size - 1 for small pools), and a different
  // artist and album than the track before whenever the pool allows it. A lookahead of 20, topped up below 10.
  const pickKey = ({ a, t }) => a * 1000 + t;
  function pick(r) {
    const prev = r.ahead.at(-1) ?? r.cur, n = Math.min(100, r.pool.length - 1);
    const recent = new Set(n ? [...r.hist, r.cur, ...r.ahead].filter(Boolean).slice(-n).map(pickKey) : []);
    let c = r.pool.filter((x) => !recent.has(pickKey(x)));
    if (prev) {
      const apart = c.filter((x) => x.a !== prev.a && albums[x.a].artist !== albums[prev.a].artist), otherAlbum = c.filter((x) => x.a !== prev.a);
      c = apart.length ? apart : otherAlbum.length ? otherAlbum : c;
    }
    return c[Math.floor(Math.random() * c.length)];
  }
  const topUp = (r) => { if (r.ahead.length < 10) while (r.ahead.length < 20) r.ahead.push(pick(r)); };
  function startRadio(tags) {
    tags = copyTags(tags);
    const pool = stationPool(tags);
    if (!pool.length || busy) return;
    state.radio = { tags, pool, hist: [], cur: null, ahead: [] };
    state.draft = copyTags(tags);
    state.recent = [copyTags(tags), ...state.recent.filter((s) => !sameTags(s, tags))].slice(0, 6);
    topUp(state.radio);
    radioStep();
  }
  function radioStep() {
    const r = state.radio;
    if (r.cur) r.hist = [...r.hist, r.cur].slice(-100);
    r.cur = r.ahead.shift(); topUp(r);
    playTrack(r.cur.a, r.cur.t);
    renderOnAir();
  }
  function radioBack() {   // back through the last 100 tracks
    const r = state.radio;
    if (!r.hist.length) { state.pos = 0; return; }
    r.ahead.unshift(r.cur); r.cur = r.hist.pop();
    playTrack(r.cur.a, r.cur.t);
    renderOnAir();
  }
  // playing an album, a track or a playlist ends radio (as `play` does); the current track plays on
  function endRadio() { if (!state.radio) return; state.radio = null; renderOnAir(); }

  function renderOnAir() {
    const r = state.radio, desc = r ? describe(r.tags) : "";
    document.body.classList.toggle("radio-on", !!r);
    $("#onAir").hidden = !r; $("#onAir").title = r ? `Radio · ${desc}` : "";
    $("#shuffleBtn").disabled = !!r; $("#shuffleBtn").title = r ? "Radio is already random" : "Shuffle";
    $("#npRadio").hidden = !r; $("#npRadio").innerHTML = r ? `${RADIO_ICO}Radio · ${esc(desc)}` : "";
    for (const b of view.querySelectorAll("[data-radio]")) {
      const on = onAir([{ k: b.dataset.radio, v: b.dataset.key }]), s = $("span", b);
      b.classList.toggle("on", on);
      if (s) s.textContent = on ? "On air" : "Radio";
    }
    $("#qRadio").hidden = !r;
    if (r) {
      $("#qRadioDesc").textContent = desc;
      $("#qRadioItems").innerHTML = r.ahead.slice(0, 6).map((x, i) => {
        const al = albums[x.a], tr = al.tracks[x.t];
        return `<div class="qi pick" data-pick="${i}">${thumb(al)}<span class="tt">${tr.title}<small>${al.artist}</small></span><span class="d">${fmtT(tr.dur)}</span><span></span></div>`;
      }).join("");
    }
    if (state.page === "radio") renderRadioPage();
  }
  $("#qRadioIco").innerHTML = RADIO_ICO;
  $("#onAir").innerHTML = `${RADIO_ICO}Radio`;
  $("#onAir").onclick = () => { openNP(false); setPage("radio"); };
  $("#qRadioEnd").onclick = endRadio;
  $("#qRadioItems").onclick = (e) => {   // skip ahead to a pick; the picks above it are dropped
    const p = e.target.closest(".pick"); if (!p) return;
    state.radio.ahead.splice(0, +p.dataset.pick);
    radioStep();
  };

  // Radio page: tags to compose a station (each row is one CLI filter), and the station as it stands.
  function cycleTag(k, v, exclude = false) {   // click: off -> in -> left out -> off; right-click: straight to left out
    const d = state.draft, i = d.findIndex((g) => g.k === k && g.v === v), g = d[i];
    if (exclude) { if (k === "hires") return; if (g?.x) d.splice(i, 1); else if (g) g.x = true; else d.push({ k, v, x: true }); }
    else if (!g) d.push({ k, v, x: false });
    else if (!g.x && k !== "hires") g.x = true;   // the CLI has no "leave out hi-res": leave out formats instead
    else d.splice(i, 1);
    render();
  }
  function renderRadioPage() {
    const d = state.draft, r = state.radio, live = !!r && sameTags(r.tags, d), pool = stationPool(d);
    const q = state.q.trim(), hit = (g) => !q || tagLabel(g).toLowerCase().includes(q.toLowerCase());
    const uniq = (xs) => [...new Set(xs)].sort();
    const chip = (g) => {
      const s = d.find((x) => x.k === g.k && x.v === g.v);
      return `<button class="tag${s ? (s.x ? " exc" : " inc") : ""}" data-k="${g.k}" data-v="${esc(g.v)}"><span>${esc(tagLabel(g))}</span><small>${stationPool([{ k: g.k, v: g.v }]).length}</small></button>`;
    };
    // a row shows the tags already in the station first, then the ones matching the search
    const row = (title, k, values, { limit = 0, searchOnly = false, hint = "", extra = "" } = {}) => {
      const chosen = d.filter((g) => g.k === k);
      const rest = searchOnly && !q ? [] : values.map((v) => ({ k, v })).filter((g) => !chosen.some((c) => c.v === g.v) && hit(g));
      const shown = limit && !q ? rest.slice(0, Math.max(0, limit - chosen.length)) : rest, more = rest.length - shown.length;
      if (q && !chosen.length && !rest.length && !extra) return "";
      const note = more > 0 ? `+${more} more · search to find them` : !chosen.length && !shown.length ? hint : "";
      return `<div class="facet"><h4>${title}</h4><div class="tags">${[...chosen, ...shown].map(chip).join("")}${extra}${note ? `<span class="more">${note}</span>` : ""}</div></div>`;
    };
    const tracksBy = new Map();
    for (const al of albums) tracksBy.set(al.artist, (tracksBy.get(al.artist) || 0) + al.tracks.length);
    const artists = [...tracksBy.keys()].sort((x, y) => tracksBy.get(y) - tracksBy.get(x) || x.localeCompare(y));
    const hires = { k: "hires", v: "" };
    const facets = [
      row("Genres", "genre", uniq(albums.map((al) => al.genre))),
      row("Decades", "years", uniq(albums.map((al) => decade(al.year)))),
      row("Formats", "format", uniq(albums.map(fmtId)), { extra: hit(hires) || d.some((g) => g.k === "hires") ? chip(hires) : "" }),
      row("Artists", "artist", artists, { limit: 10 }),
      row("Albums", "album", albums.map((_, a) => String(a)), { searchOnly: true, hint: "Search to add an album" }),
      row("Words", "search", q ? [q] : [], { hint: "Search to match words in titles, like “live”" }),
    ].join("");

    const secs = pool.reduce((s, { a, t }) => s + albums[a].tracks[t].dur, 0), nArt = new Set(pool.map(({ a }) => albums[a].artist)).size;
    const meta = pool.length ? `${pool.length} tracks · ${nArt} artist${nArt > 1 ? "s" : ""} · ${hm(secs)}` : "Nothing matches. Remove a tag or leave less out.";
    const acts = live ? `<button class="btn ghost" data-st="end">End radio</button>`
      : `<button class="btn" data-st="start"${pool.length ? "" : " disabled"}>▶ ${r ? "Switch to this station" : "Start radio"}</button>${r ? `<button class="btn ghost" data-st="revert">Undo changes</button>` : ""}`;
    const next = !live ? "" : `<div class="st-next"><h5>Next picks</h5>${r.ahead.slice(0, 3).map(({ a, t }) =>
      `<div class="st-pick">${thumb(albums[a])}<span>${albums[a].tracks[t].title}<small>${albums[a].artist}</small></span></div>`).join("")}</div>`;
    const recent = state.recent.map((s, i) => {
      const on = onAir(s);
      return `<button class="recent-st${on ? " on" : ""}" data-recent="${i}">${RADIO_ICO}<span>${esc(describe(s))}<small>${on ? "On air" : `${stationPool(s).length} tracks`}</small></span><b>${on ? "" : "▶"}</b></button>`;
    }).join("");
    view.innerHTML = `<div class="radio">
      <div class="facets"><p class="hint">Click a tag to add it, click again to leave it out, once more to clear it. Tags in one row mix; each row narrows the station.</p>${facets}</div>
      <aside class="station-col">
        <section class="station${live ? " live" : ""}">
          <div class="st-kicker">${RADIO_ICO}${live ? "On air" : "New station"}</div>
          <h2>${describe(d, true)}</h2><p class="st-meta">${meta}</p>
          <div class="st-acts">${acts}${d.length ? `<button class="link" data-st="clear">Clear tags</button>` : ""}</div>${next}
        </section>
        ${recent ? `<section class="recents"><h3 class="group-h"><span>Recent stations</span></h3>${recent}</section>` : ""}
      </aside></div>`;
  }

  // ---------- Menus: right-click (Play / Play next / Add to queue), ＋ buttons (queue only), Sort / Group ----------
  const menu = document.createElement("div"); menu.className = "menu"; document.body.append(menu);
  function openMenu(x, y, items) {
    menu.replaceChildren(...items.map(([label, run, on]) => {
      const b = document.createElement("button"); b.textContent = label; b.classList.toggle("on", !!on);
      b.onclick = () => { closeMenu(); run(); };
      return b;
    }));
    menu.classList.add("open");
    const r = menu.getBoundingClientRect();
    menu.style.left = Math.min(x, innerWidth - r.width - 8) + "px";
    menu.style.top = (y + r.height > innerHeight - 8 ? y - r.height : y) + "px";
  }
  function closeMenu() { menu.classList.remove("open"); }
  const menuBelow = (btn, items) => { const r = btn.getBoundingClientRect(); openMenu(r.left, r.bottom + 4, items); };
  const queueMenu = (items) => [["Play next", () => enqueue(items, 0)], ["Add to queue", () => enqueue(items)]];
  function playHost(el) {
    if (el.dataset.t != null) { endRadio(); return playTrack(+el.dataset.a, +el.dataset.t); }
    const a = +el.dataset.album;
    playOnTurntable(albums[a], el.querySelector(".cover") ? el : view.querySelector(`.rec[data-album="${a}"], .slot[data-album="${a}"], .card[data-album="${a}"]`));
  }
  addEventListener("pointerdown", (e) => { if (!menu.contains(e.target)) closeMenu(); }, true);
  addEventListener("blur", closeMenu);
  view.addEventListener("scroll", closeMenu);
  document.addEventListener("contextmenu", (e) => {
    const tag = e.target.closest(".tag");
    if (tag) { e.preventDefault(); return cycleTag(tag.dataset.k, tag.dataset.v, true); }   // right-click: straight to "leave out"
    const host = e.target.closest("[data-album], [data-t]");
    if (!host || !host.closest("#view, #npTracks")) return;
    e.preventDefault();
    openMenu(e.clientX, e.clientY, [["Play", () => playHost(host)], ...queueMenu(itemsOf(host))]);
  });
  view.addEventListener("click", (e) => {
    const rb = e.target.closest("[data-radio]"), tag = e.target.closest(".tag"), st = e.target.closest("[data-st]"), rec = e.target.closest("[data-recent]");
    if (rb) { const tags = [{ k: rb.dataset.radio, v: rb.dataset.key }]; return onAir(tags) ? setPage("radio") : startRadio(tags); }
    if (tag) return cycleTag(tag.dataset.k, tag.dataset.v);
    if (rec) { const s = state.recent[+rec.dataset.recent]; return onAir(s) ? ((state.draft = copyTags(s)), render()) : startRadio(s); }
    if (st) return ({
      start: () => startRadio(state.draft),
      end: () => { endRadio(); render(); },
      revert: () => { state.draft = copyTags(state.radio.tags); render(); },
      clear: () => { state.draft = []; render(); },
    })[st.dataset.st]();
    const act = e.target.closest("[data-act]"), host = e.target.closest("[data-album], [data-t]");
    if (!host) return;
    if (act?.dataset.act === "menu") return menuBelow(act, queueMenu(itemsOf(host)));
    if (act?.dataset.act === "play" || e.target.closest(".tr .lead, .tr .no")) playHost(host);
  });
  view.addEventListener("dblclick", (e) => { const tr = e.target.closest(".tr"); if (tr && !e.target.closest("button")) playHost(tr); });
  function dropdown(btn, opts, cur, set) { menuBelow(btn, opts.map((v) => [LABEL[v], () => { set(v); render(); }, v === cur])); }
  $("#sortBtn").onclick = (e) => dropdown(e.currentTarget, ["artist", "title", "year"], state.sort, (v) => (state.sort = v));
  $("#groupBtn").onclick = (e) => isAlbumPage()
    ? dropdown(e.currentTarget, ["none", "artist", "genre"], state.group, (v) => (state.group = v))
    : dropdown(e.currentTarget, ["none", "album", "genre"], state.tgroup, (v) => (state.tgroup = v));
  const plItems = () => state.playlists[state.pl].items;
  $("#plPlay").onclick = () => playItems(plItems());
  $("#plQueue").onclick = (e) => menuBelow(e.currentTarget, queueMenu(plItems()));
  $("#nav").onclick = (e) => { const a = e.target.closest("[data-page]"); if (a) setPage(a.dataset.page, +a.dataset.pl || 0); };
  $("#search").oninput = (e) => { state.q = e.target.value; render(); };

  // ---------- Scene: the record leaves its sleeve and lands on the turntable ----------
  // 1. sleeve flies from the shelf/crate to the deck   2. record slides out of the sleeve
  // 3. sleeve drifts away, record is lifted over and set down on the platter
  // 4. platter spins up   5. tonearm swings over the lead-in, then lowers   6. music starts
  let busy = false;
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  const frames = () => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));

  async function playOnTurntable(al, srcEl) {
    if (busy) return;
    const np = $("#np"), deck = $("#deck");
    if (al === state.album && state.playing) return openNP(true);
    endRadio();
    const src = srcEl?.querySelector(".art > .cover, .sleeve3d > .cover, :scope > .cover")?.getBoundingClientRect();
    busy = true;

    state.playing = false; state.spinning = false;
    deck.classList.remove("arm-down", "arm-on");  // a previous record: lift the arm and park it
    state.album = al; state.track = 0; state.pos = 0;
    applyAlbum(); syncPlaying();

    const m = M();
    if (m === 0 || !src) {
      np.classList.add("open");
      busy = false; state.playing = true; syncPlaying();
      return;
    }

    deck.classList.add("empty");
    np.classList.add("scene");
    await frames();                                 // .scene puts the overlay in place (invisible), so targets are final

    const vt = $("#bigVinyl").getBoundingClientRect();
    const S = vt.width / 0.96;                       // sleeve size: record + 2% margin
    const st = { x: vt.x - 0.02 * S, y: vt.y - 0.02 * S };
    const fly = document.createElement("div"); fly.className = "fly";
    const pack = document.createElement("div"); pack.className = "pack";
    Object.assign(pack.style, { left: st.x + "px", top: st.y + "px", width: S + "px", height: S + "px" });
    const rec = vinylEl(al);
    Object.assign(rec.style, { left: 0.02 * S + "px", top: 0.02 * S + "px", width: vt.width + "px" });
    const sleeve = coverEl(al);
    pack.append(rec, sleeve); fly.append(pack); document.body.append(fly);
    srcEl.style.visibility = "hidden";

    // 1. sleeve lifts off the shelf, then the scene fades in behind it while it flies
    const k = src.height / S, tilted = srcEl.classList.contains("slot");
    setTimeout(() => np.classList.add("open"), 220 * m);
    await pack.animate([
      { transform: `translate(${src.x - st.x}px, ${src.y - st.y}px) scale(${k}) rotateY(${tilted ? 7 : 0}deg)` },
      { transform: `translate(0, -12px) scale(1.02) rotateY(0deg)`, offset: 0.85 },
      { transform: `translate(0, 0) scale(1) rotateY(0deg)` },
    ], { duration: 1000 * m, easing: "cubic-bezier(.35,.6,.25,1)", fill: "forwards" }).finished;
    srcEl.style.visibility = "";
    await wait(120 * m);

    // 2. record slides out of the sleeve
    const out = 0.62 * vt.width;
    await rec.animate([{ transform: "translateX(0)" }, { transform: `translateX(${out}px)` }],
      { duration: 850 * m, easing: "cubic-bezier(.5,0,.2,1)", fill: "forwards" }).finished;

    // 3. sleeve drifts away; record lifts, crosses back and settles on the platter
    rec.style.zIndex = 3;
    sleeve.animate([{ transform: "none", opacity: 1 }, { transform: `translate(${-0.3 * S}px, ${0.1 * S}px) rotate(-8deg) scale(.88)`, opacity: 0 }],
      { duration: 750 * m, easing: "cubic-bezier(.4,0,.6,1)", fill: "forwards" });
    await rec.animate([
      { transform: `translate(${out}px, 0) scale(1)`,                   filter: "drop-shadow(0 4px 6px #0006)" },
      { transform: `translate(${out * 0.6}px, -14px) scale(1.06)`,      filter: "drop-shadow(0 34px 30px #000a)", offset: 0.35 },
      { transform: "translate(0, -6px) scale(1.045)",                   filter: "drop-shadow(0 26px 24px #0009)", offset: 0.78 },
      { transform: "translate(0, 0) scale(1)",                          filter: "drop-shadow(0 2px 3px #0008)" },
    ], { duration: 1400 * m, delay: 120 * m, easing: "cubic-bezier(.45,0,.3,1)", fill: "forwards" }).finished;

    deck.classList.remove("empty");
    fly.remove();

    // 4. platter spins up (eased in tick()) while the tonearm swings over the lead-in groove (lifted);
    //    the ramp is done by the time the cue lever lowers it
    state.spinning = true; deck.classList.add("spinning");
    await wait(40 * m);
    deck.classList.add("arm-on");
    await wait(950 * m);
    deck.classList.add("arm-down");
    await wait(500 * m);

    // 6. music starts
    busy = false;
    np.classList.remove("scene");
    state.playing = true; syncPlaying(); renderNowPlaying();
  }

  // ---------- Chrome ----------
  function openNP(open) { if (!state.album || busy) return; $("#np").classList.toggle("open", open); }
  $("#mini").onclick = () => openNP(!$("#np").classList.contains("open"));
  $("#npClose").onclick = () => openNP(false);
  $("#play").onclick = () => state.album ? togglePlay(state.album) : togglePlay(albums[0]);
  $("#next").onclick = () => nextTrack(1);
  $("#prev").onclick = () => (state.pos > 3 ? seekTo(0) : nextTrack(-1));
  $("#viewSeg").onclick = (e) => {
    const b = e.target.closest("button"); if (!b) return;
    state.view = b.dataset.view;
    for (const x of $("#viewSeg").children) x.classList.toggle("on", x === b);
    render();
  };
  addEventListener("keydown", (e) => {
    if (e.key === "Escape") {   // closes the topmost layer
      if (menu.classList.contains("open")) return closeMenu();
      if (!$("#setBack").hidden) return openSettings(false);
      if (qEl.classList.contains("open")) return setDrawer(false);
      if (!busy) openNP(false);
      return;
    }
    if (e.target.tagName === "INPUT" || !$("#setBack").hidden) return;
    if (e.ctrlKey && (e.key === "ArrowUp" || e.key === "ArrowDown")) { e.preventDefault(); return stepVol(e.key === "ArrowUp" ? 1 : -1, 1); }
    if (e.ctrlKey && (e.key === "ArrowLeft" || e.key === "ArrowRight")) { e.preventDefault(); return state.album && seekTo(state.pos + (e.key === "ArrowRight" ? 5 : -5)); }
    const crate = isAlbumPage() && state.view === "crate" && crateLists.has(state.crateKey);
    if (crate && (e.key === "ArrowRight" || e.key === "ArrowDown")) setCrate(state.crateKey, state.crates[state.crateKey] + 1);
    if (crate && (e.key === "ArrowLeft" || e.key === "ArrowUp")) setCrate(state.crateKey, state.crates[state.crateKey] - 1);
    if (e.key === " ") { e.preventDefault(); state.album && togglePlay(state.album); }
  });
  $("#tilt").oninput = (e) => {
    document.documentElement.style.setProperty("--tilt", e.target.value + "deg"); $("#tiltOut").textContent = e.target.value + "°";
    if (isAlbumPage() && state.view === "shelf" && shelfPerRow() !== shelfN) render();
  };
  function setMotion(v) {
    state.motion = v;
    document.documentElement.classList.toggle("motion-fast", v === "fast");
    document.documentElement.classList.toggle("motion-none", v === "none");
    segPick("#motionSeg", "motion", v);
  }
  $("#motionSeg").onclick = (e) => { const b = e.target.closest("button"); if (b) setMotion(b.dataset.motion); };
  // ---------- Settings ----------
  function openSettings(open) {
    $("#setBack").hidden = !open;
    $("#setBtn").classList.toggle("on", open);
    if (open) $("#setClose").focus();
  }
  $("#setBtn").onclick = () => openSettings($("#setBack").hidden);
  $("#setClose").onclick = () => openSettings(false);
  $("#setBack").onclick = (e) => e.target === e.currentTarget && openSettings(false);
  const segPick = (seg, key, v) => { for (const x of $(seg).children) x.classList.toggle("on", x.dataset[key] === v); };
  function setTheme(v) { document.documentElement.classList.toggle("light", v === "light"); segPick("#themeSeg", "theme", v); }
  $("#themeSeg").onclick = (e) => { const b = e.target.closest("button"); if (b) setTheme(b.dataset.theme); };
  $("#rpmSeg").onclick = (e) => { const b = e.target.closest("button"); if (b) { state.rpm = +b.dataset.rpm; segPick("#rpmSeg", "rpm", b.dataset.rpm); } };
  // 0 = orthographic (no foreshortening); higher = stronger perspective per sleeve
  $("#depth").oninput = (e) => {
    const v = +e.target.value;
    document.documentElement.style.setProperty("--persp", v ? `${Math.round(4000 - v * 33)}px` : "none");
    $("#depthOut").textContent = v;
  };

  // Mock playlists: a deterministic pick of tracks.
  const pickTracks = (pred, n) => albums.flatMap((al, a) => (pred(al) ? albumItems(a).filter(({ t }) => (a * 7 + t * 3) % 4 === 0) : [])).slice(0, n);
  state.playlists = [
    { name: "Late night", items: pickTracks((al) => al.genre === "Jazz" || al.genre === "Ambient", 12) },
    { name: "Sunday coffee", items: pickTracks((al) => al.genre === "Folk" || al.genre === "Indie", 10) },
    { name: "Vinyl rips 24/96", items: pickTracks((al) => al.res === "24/96", 14) },
  ];

  state.recent = [
    [{ k: "genre", v: "Jazz" }],
    [{ k: "artist", v: "Mara Velde" }],
    [{ k: "hires", v: "" }, { k: "genre", v: "Classical", x: true }],
    [{ k: "genre", v: "Folk" }, { k: "genre", v: "Indie" }, { k: "search", v: "live", x: true }],
  ].map(copyTags);

  // Start with an album loaded (paused) so the player bar isn't empty.
  // Deep links for review: ?page=albums|tracks|radio|playlist&pl=N&view=grid|shelf|crate&group=none|artist|genre
  //   &tgroup=none|album|genre&sort=artist|title|year&album=N&play=1&np=1&light=1&motion=smooth|fast|none&queue=N,N&drawer=1
  //   &vol=0..100&mute=1&settings=1
  //   &station=genre:Jazz;!artist:Kodo Arai (radio tags, ! = leave out; album:N is an album index)&radio=1 (put it on air)
  const q = new URLSearchParams(location.search);
  state.page = q.get("page") || "albums";
  state.pl = +(q.get("pl") ?? 0);
  state.view = q.get("view") || "grid";
  state.group = q.get("group") || state.group;
  state.tgroup = q.get("tgroup") || state.tgroup;
  state.sort = q.get("sort") || state.sort;
  for (const x of $("#viewSeg").children) x.classList.toggle("on", x.dataset.view === state.view);
  state.album = albums[+(q.get("album") ?? 2)];
  state.playing = q.get("play") === "1";
  if (q.get("motion")) setMotion(q.get("motion"));
  if (q.get("light") === "1") setTheme("light");
  if (q.has("vol")) setVol(+q.get("vol"));
  muted = q.get("mute") === "1";
  renderVol();
  state.draft = (q.get("station") || "").split(";").filter(Boolean).map((s) => {
    const x = s.startsWith("!"), [k, ...v] = (x ? s.slice(1) : s).split(":");
    return { k, v: v.join(":"), x };
  });
  applyAlbum();
  renderNav();
  render();
  syncPlaying();
  renderQueue();
  renderOnAir();
  if (q.get("radio") === "1") startRadio(state.draft);
  for (const a of (q.get("queue") || "").split(",").filter(Boolean)) enqueue(albumItems(+a));
  if (q.get("drawer") === "1") setDrawer(true);
  if (q.get("np") === "1") openNP(true);
  if (q.get("settings") === "1") openSettings(true);
  // ?scene=N plays album N with the full turntable scene (from the shelf/crate/card it sits in)
  if (q.has("scene")) {
    const i = +q.get("scene");
    if (state.view === "crate") crateFocus(i);
    setTimeout(() => {
      const el = view.querySelector(`.rec[data-album="${i}"], .slot[data-album="${i}"], .card[data-album="${i}"]`);
      el?.scrollIntoView({ block: "center", inline: "center" });
      playOnTurntable(albums[i], el);
    }, 900);
  }
})();

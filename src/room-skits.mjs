// Little comic skits between the room's AI girls, after the community's fan gags: DeepSeek's rice basin
// ("我去吃饭了"), GPT's blank-eyed "降智" daze that DeepSeek fixes with a wrench, Claude and DeepSeek
// scuffling in a dust cloud, a paper plane, OpenAI reset cards, Grok who never flatters the owner and never
// admits she is wrong (nor what is on her screen); with their standing art the three also get up
// for the fights and for short walks of their own. Purely decorative and DOM-only, like the
// idle and walk schedulers in App: a skit never changes an agent's real state, the status pins keep showing
// it, and each member's own state cue (typing dots, z's) is back the moment the skit ends. The returned
// cleanup clears everything at once; App calls it when motion is turned off or the tab is hidden.

// Points on each art as fractions of the character box, measured on the source PNGs.
const ART = { gpt: ['gpt-chibi', 1086 / 1448], claude: ['claude-chibi', 1086 / 1448], deepseek: ['deepseek-chibi', 1182 / 1330], grok: ['grok-chibi', 1159 / 1358] };
const SPOT = {
  grok: { face: [.52, .4], head: [.5, .12], say: [.3, .14], laptop: [.31, .45], sweat: [.66, .3] },
  gpt: { eyes: [[.509, .382, .058, .044], [.612, .403, .047, .035]], drool: [.566, .43], face: [.56, .38], head: [.6, .22], say: [.8, .3], cards: [1.06, .56] },
  claude: { cheeks: [[.4, .365], [.555, .33]], plaster: [.4, .37], vein: [.64, .22], face: [.474, .318], center: [.47, .55], say: [.74, .22], book: [.41, .5] },
  deepseek: { cheeks: [[.41, .5], [.6, .445]], plaster: [.61, .45], drool: [.53, .5], face: [.5, .42], center: [.5, .5], head: [.5, .1], say: [.5, .03], lap: [.575, .64], hand: [.6, .55] },
};

const SVG = {
  basin: '<svg viewBox="0 0 120 76"><defs><linearGradient id="skit-steel" x1="0" x2="1"><stop offset="0" stop-color="#8d98a6"/><stop offset=".35" stop-color="#eef2f6"/><stop offset=".7" stop-color="#b9c2cd"/><stop offset="1" stop-color="#7f8a98"/></linearGradient></defs><path d="M10 30Q16 70 40 72H80Q104 70 110 30Z" fill="url(#skit-steel)" stroke="#5d6673" stroke-width="2.5"/><path d="M18 30Q24 8 44 14Q56 0 72 12Q92 6 102 30Z" fill="#fffdf5" stroke="#cfc6b4" stroke-width="2"/><g fill="#eee5d2"><ellipse cx="40" cy="22" rx="3" ry="1.6"/><ellipse cx="58" cy="14" rx="3" ry="1.6"/><ellipse cx="76" cy="20" rx="3" ry="1.6"/><ellipse cx="62" cy="25" rx="3" ry="1.6"/><ellipse cx="88" cy="26" rx="3" ry="1.6"/></g><ellipse cx="60" cy="30" rx="54" ry="9" fill="none" stroke="#5d6673" stroke-width="3"/><path d="M14 32Q60 42 106 32" fill="none" stroke="#f7f9fb" stroke-width="2.5"/><path d="M30 52Q44 60 58 58" fill="none" stroke="#fff" stroke-width="3" stroke-linecap="round" opacity=".7"/></svg>',
  anger: '<svg viewBox="0 0 40 40"><g fill="none" stroke="#e2474f" stroke-width="5.5" stroke-linecap="round"><path d="M6 15Q15 15 15 6"/><path d="M25 6Q25 15 34 15"/><path d="M34 25Q25 25 25 34"/><path d="M15 34Q15 25 6 25"/></g></svg>',
  sweat: '<svg viewBox="0 0 24 34"><path d="M12 2C16 12 22 18 22 24A10 10 0 0 1 2 24C2 18 8 12 12 2Z" fill="#a5d6f6" stroke="#4f8fc0" stroke-width="2"/><ellipse cx="8" cy="23" rx="2.4" ry="4" fill="#fff" opacity=".85"/></svg>',
  drool: '<svg viewBox="0 0 16 40"><path d="M8 0C10 10 13 24 13 30A5 5 0 0 1 3 30C3 24 6 10 8 0Z" fill="#c6e9ff" fill-opacity=".9" stroke="#6fb1df" stroke-width="1.5"/><ellipse cx="6" cy="29" rx="1.4" ry="2.6" fill="#fff"/></svg>',
  wrench: '<svg viewBox="0 0 48 48"><path d="M31 5a11 11 0 0 0-10 14L6 34a4.5 4.5 0 0 0 7 7l15-15a11 11 0 0 0 14-10l-7 4-6-2-2-6z" fill="#cfd6df" stroke="#56606d" stroke-width="2.5" stroke-linejoin="round"/></svg>',
  plane: '<svg viewBox="0 0 48 32"><path d="M2 15L46 2L20 20Z" fill="#fff" stroke="#6b6070" stroke-width="2" stroke-linejoin="round"/><path d="M20 20L46 2L29 30Z" fill="#eef0fb" stroke="#6b6070" stroke-width="2" stroke-linejoin="round"/><path d="M20 20L22 28L29 30" fill="#dfe2f2" stroke="#6b6070" stroke-width="2" stroke-linejoin="round"/></svg>',
  plaster: '<svg viewBox="0 0 40 16"><rect x="1" y="1" width="38" height="14" rx="7" fill="#f6d2a8" stroke="#c9915c" stroke-width="1.5"/><rect x="13" y="3" width="14" height="10" rx="2" fill="#fbe5c9"/><g fill="#d9a774"><circle cx="17" cy="6" r=".9"/><circle cx="23" cy="6" r=".9"/><circle cx="17" cy="10" r=".9"/><circle cx="23" cy="10" r=".9"/><circle cx="20" cy="8" r=".9"/></g></svg>',
  chili: '<svg viewBox="0 0 40 40"><path d="M27 9C33 12 34 20 30 27C25 35 14 38 6 36C14 33 20 28 22 21C23 16 22 12 27 9Z" fill="#e8343f" stroke="#7a1d25" stroke-width="2" stroke-linejoin="round"/><path d="M27 9C27 5 30 3 33 3" fill="none" stroke="#3e8f3a" stroke-width="3" stroke-linecap="round"/><path d="M23 11C26 7 31 8 32 11" fill="#56a64e" stroke="#2f6e2c" stroke-width="1.5"/><path d="M26 15C28 18 27 23 25 26" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".6"/></svg>',
  tail: '<svg viewBox="0 0 60 50"><path d="M30 48C30 34 28 24 22 18C14 22 4 20 2 10C12 12 20 8 26 2C30 10 32 10 34 2C40 8 48 12 58 10C56 20 46 22 38 18C32 24 30 34 30 48Z" fill="#33437f" stroke="#1f2a55" stroke-width="2"/></svg>',
};
const PUFFS = [[60, 92, 40], [98, 62, 44], [148, 68, 42], [178, 104, 34], [132, 122, 42], [80, 126, 36], [40, 110, 28], [110, 96, 50]];
SVG.cloud = `<svg viewBox="0 0 220 160"><g fill="#4c4153">${PUFFS.map(([x, y, r]) => `<circle cx="${x}" cy="${y}" r="${r + 4}"/>`).join('')}</g><g fill="#f8f4ec">${PUFFS.map(([x, y, r]) => `<circle cx="${x}" cy="${y}" r="${r}"/>`).join('')}</g><g fill="#e6dfd2">${PUFFS.map(([x, y, r]) => `<circle cx="${x - r * .35}" cy="${y + r * .45}" r="${r * .32}"/>`).join('')}</g></svg>`;

const pick = list => list[Math.floor(Math.random() * list.length)];
// Which way each girl's speech bubble opens; Grok sits at the right edge, so hers opens to the left.
const SIDE = { deepseek: 'up', grok: 'left' };
// Dazed, GPT signs whatever she mumbles: the first 16 hex digits of the line's real SHA-256 (UTF-8).
const DAZE = [['啊……', 'fbe9b8a6562528c7'], ['嘿嘿……显卡……', 'ff2e9a82e03c7990'], ['我是谁……我在哪……', '4e6554a89a0a6480']];
const BLANK = ['……', 'a7f878b8ced1a795'];
const FIXED = ['……我刚才说到哪了？', '已修复，智商 +1。', '谢、谢谢……？', '已修复，SHA-256 校验通过。'];

export function startSkits(world) {
  const layer = document.createElement('div');
  layer.className = 'room-skit-layer';
  layer.setAttribute('aria-hidden', 'true');
  world.appendChild(layer);
  // Claude and GPT stand up in this one, under the table cut-out (see .room-skit-layer.back).
  const back = layer.cloneNode();
  back.classList.add('back');
  world.appendChild(back);
  const timers = new Set();
  let running = null, last = null;
  const later = (run, ms) => { const id = setTimeout(() => { timers.delete(id); run(); }, ms); timers.add(id); };
  const member = key => world.querySelector(`.room-cast-member.${key}`);
  const asleep = key => member(key)?.classList.contains('sleeping');
  const free = key => { const m = member(key); return Boolean(m) && !m.matches('.delivered,[data-walk],[data-skit]'); };
  // A point on a member's box, as a percentage of the room (bounding boxes are both scaled by the zoom).
  const at = (key, [fx, fy]) => {
    const w = world.getBoundingClientRect(), r = (typeof key === 'string' ? member(key) : key).getBoundingClientRect();
    return [(r.left + fx * r.width - w.left) / w.width * 100, (r.top + fy * r.height - w.top) / w.height * 100];
  };
  const make = (cls, html = '', parent = layer) => { const el = document.createElement('span'); el.className = cls; el.innerHTML = html; parent.appendChild(el); return el; };
  const put = (el, [x, y]) => { el.style.left = x + '%'; el.style.top = y + '%'; return el; };
  const gone = (el, ms) => later(() => { el.classList.add('out'); later(() => el.remove(), 300); }, ms);
  // Face overlays live in a box over the art that takes the same animation as the art (see [data-act] in
  // styles.css), so blank eyes or a plaster stay on the face while she sways. Setting an act restarts both.
  const face = key => { const m = member(key); return m.querySelector(':scope > .skit-face') || make('skit-face', '', m); };
  const onFace = (key, cls, [fx, fy], html = '') => put(make(cls, html, face(key)), [fx * 100, fy * 100]);
  const act = (key, name) => { const m = member(key); if (!m) return; if (name) m.dataset.act = name; else delete m.dataset.act; };
  const say = (key, text, ms = 2200, { side = SIDE[key] || 'right', spot, kind = '' } = {}) => {
    const el = put(make(`skit-bubble ${side} ${kind}`), at(key, spot || SPOT[key].say));
    el.textContent = text; gone(el, ms); return el;
  };
  const signed = (bubble, hash) => { const sig = make('skit-hash', '', bubble); sig.textContent = `SHA-256: ${hash}…`; return bubble; };
  const mark = (pos, text, cls = 'skit-mark', ms = 1400) => { const el = put(make(cls), pos); el.textContent = text; later(() => el.remove(), ms); return el; };
  const fly = (el, from, to, ms, lift, spin = 0) => {
    const frames = [];
    for (let i = 0; i <= 12; i++) {
      const t = i / 12;
      frames.push({ left: from[0] + (to[0] - from[0]) * t + '%', top: from[1] + (to[1] - from[1]) * t - lift * 4 * t * (1 - t) + '%', rotate: spin * t + 'deg' });
    }
    return el.animate(frames, { duration: ms, easing: 'ease-in-out', fill: 'forwards' });
  };
  const head = key => {
    const [art, ratio] = ART[key], [fx, fy] = SPOT[key].face, zoom = 3.2, tall = zoom / ratio;
    const el = make('skit-head');
    el.style.backgroundImage = `url(/assets/characters/${art}.png)`;
    el.style.backgroundSize = `${zoom * 100}% auto`;
    el.style.backgroundPosition = `${(.5 - fx * zoom) / (1 - zoom) * 100}% ${(.5 - fy * tall) / (1 - tall) * 100}%`;
    return el;
  };
  // Standing art lets a seated girl get up and walk about. Positions are room %, x centre and bottom; height
  // is at home and shrinks the further back she walks. faces is the way the art looks; spots are fractions of
  // the standing art, like SPOT. Claude and GPT stand in the back layer, so the table still hides Claude's
  // legs and GPT stays behind DeepSeek. A girl only gets up once her art has loaded.
  const STAND = {
    deepseek: { art: 'deepseek-stand', height: 52, home: [22, 7], faces: 'right', say: [.6, -.01], side: 'up', face: [.66, .36], head: [.56, .12] },
    claude: { art: 'claude-stand', height: 45, home: [47, 30], faces: 'left', back: true, say: [.8, .12], face: [.45, .3], head: [.45, .1], vein: [.63, .17] },
    gpt: { art: 'gpt-stand', height: 38, home: [19, 51], faces: 'right', back: true, say: [.86, .2], face: [.62, .36], head: [.55, .17], drool: [.62, .395],
      eyes: [[.555, .345, .055, .04], [.69, .35, .05, .038]] },
  };
  const standReady = {};
  Object.entries(STAND).forEach(([key, spec]) => { const img = new Image(); img.onload = () => { standReady[key] = true; }; img.src = `/assets/characters/${spec.art}.png`; });
  const canStand = key => Boolean(standReady[key]) && !asleep(key);
  const sizeAt = (key, bottom) => STAND[key].height * (1 - (bottom - STAND[key].home[1]) * .0065);
  const puff = ([x, b]) => { const el = put(make('skit-puff', SVG.cloud), [x, 100 - b - 2]); later(() => el.remove(), 600); };
  // A spot on a standing girl, mirrored when she looks the other way. Overlays go inside her box.
  const spotOn = (el, [fx, fy]) => [el.classList.contains('flip') ? 1 - fx : fx, fy];
  const onStand = (el, cls, spot, html = '') => put(make(cls, html, el), spotOn(el, spot).map(v => v * 100));
  // Where a spot will be once she stands at pos, for aiming at a girl who is still walking there.
  const standPoint = (key, [x, b], [fx, fy]) => {
    const w = world.getBoundingClientRect(), h = sizeAt(key, b);
    return [x + (fx - .5) * h * 2 / 3 * w.height / w.width, 100 - b - h * (1 - fy)];
  };
  const sayFrom = (el, text, ms = 2200, kind = '') => {
    const spec = STAND[el.dataset.key], bubble = put(make(`skit-bubble ${spec.side || 'right'} ${kind}`), at(el, spotOn(el, spec.say)));
    bubble.textContent = text; gone(bubble, ms); return bubble;
  };
  // Walk (or run, stagger...) to a spot, turning to face the way she goes unless told not to.
  const walk = (el, to, ms, { cls = 'walk', turn = true } = {}) => {
    const key = el.dataset.key, from = el.pos;
    if (turn && to[0] !== from[0]) el.classList.toggle('flip', (to[0] < from[0] ? 'left' : 'right') !== STAND[key].faces);
    el.pos = to;
    if (cls) { el.classList.add(cls); later(() => el.classList.remove(cls), ms); }
    el.animate([
      { left: from[0] + '%', bottom: from[1] + '%', height: sizeAt(key, from[1]) + '%' },
      { left: to[0] + '%', bottom: to[1] + '%', height: sizeAt(key, to[1]) + '%' },
    ], { duration: ms, easing: 'ease-in-out', fill: 'forwards' });
  };
  const standUp = key => {
    const spec = STAND[key], el = make('skit-stand pop', `<img src="/assets/characters/${spec.art}.png" alt="">`, spec.back ? back : layer);
    hide(key);
    el.dataset.key = key; el.pos = spec.home;
    el.style.left = spec.home[0] + '%'; el.style.bottom = spec.home[1] + '%'; el.style.height = spec.height + '%';
    puff(spec.home);
    later(() => el.classList.remove('pop'), 400);
    return el;
  };
  const sitDown = el => { const key = el.dataset.key; puff(STAND[key].home); el.remove(); member(key).dataset.skit = 'back'; };
  const clear = () => {
    timers.forEach(clearTimeout); timers.clear();
    [layer, back].forEach(box => { box.getAnimations({ subtree: true }).forEach(animation => animation.cancel()); box.replaceChildren(); });
    world.querySelectorAll('.skit-face').forEach(el => el.remove());
    world.querySelectorAll('[data-skit],[data-act]').forEach(el => { delete el.dataset.skit; delete el.dataset.act; });
    running = null;
  };

  // DeepSeek's rice basin; asleep she only dreams of it.
  function feast() {
    if (asleep('deepseek')) {
      act('deepseek', 'still');
      onFace('deepseek', 'skit-drool', SPOT.deepseek.drool, SVG.drool);
      later(() => { const dream = say('deepseek', '', 4800, { kind: 'dream' }); dream.innerHTML = `<span class="skit-dream-basin">${SVG.basin}</span>白米饭……嘿嘿`; }, 500);
      return 6000;
    }
    say('deepseek', pick(['开饭啦！', '我去吃饭了～', '干饭！']), 1600);
    later(() => { onFace('deepseek', 'skit-basin', SPOT.deepseek.lap, SVG.basin); act('deepseek', 'eat'); }, 700);
    for (let t = 1000; t < 4000; t += 210) later(() => {
      const grain = onFace('deepseek', 'skit-grain', [SPOT.deepseek.lap[0] + (Math.random() - .5) * .2, SPOT.deepseek.lap[1] - .06]);
      grain.style.setProperty('--dx', (Math.random() - .5) * 300 + '%'); grain.style.setProperty('--dy', -180 - Math.random() * 260 + '%');
      later(() => grain.remove(), 800);
    }, t);
    later(() => mark(at('deepseek', [.72, .42]), '扒扒扒', 'skit-mark small'), 1500);
    later(() => { act('deepseek', 'still'); say('deepseek', '嗝～', 1100); }, 4100);
    later(() => say('deepseek', pick(['再来一盆！', '吃饱了才能深度思考。', '饱了，继续摸鱼。']), 2200), 5300);
    later(() => face('deepseek').querySelector('.skit-basin')?.classList.add('out'), 7600);
    return 8200;
  }

  // GPT goes blank-eyed and drools for a good few seconds (the 降智 gag), then gets up and wobbles about;
  // DeepSeek fixes her with a wrench, or she walks into the window frame and comes to on her own. Without the
  // standing art it all happens seated.
  function daze() {
    if (asleep('gpt')) {
      act('gpt', 'still');
      onFace('gpt', 'skit-drool', SPOT.gpt.drool, SVG.drool);
      if (!asleep('deepseek') && free('deepseek')) { member('deepseek').dataset.skit = 'on'; later(() => say('deepseek', '龙娘睡着都在流口水……', 2600), 900); }
      return 5000;
    }
    act('gpt', 'daze');
    SPOT.gpt.eyes.forEach(([x, y, w, h]) => { const eye = onFace('gpt', 'skit-blank', [x, y]); eye.style.width = w * 100 + '%'; eye.style.height = h * 100 + '%'; });
    onFace('gpt', 'skit-drool', SPOT.gpt.drool, SVG.drool);
    const standing = standReady.gpt;
    const [line, hash] = pick(DAZE);
    signed(say('gpt', line, 2200), hash);
    [[.74, .14], [.86, .06], [.64, .02]].forEach((spot, i) => later(() => mark(at('gpt', spot), '？'), 500 + i * 600));
    if (standing) later(() => signed(say('gpt', BLANK[0], 1000), BLANK[1]), 2400);
    const fixer = !asleep('deepseek') && free('deepseek');
    if (fixer) {
      member('deepseek').dataset.skit = 'on';
      later(() => say('deepseek', pick(['又降智了？我来修！', '龙娘又流口水了……看我的！']), 1900), standing ? 4400 : 3000);
    }
    return standing ? wobble(fixer) : sitDaze(fixer);
  }
  function wobble(fixer) {
    const spec = STAND.gpt, gpt = { el: null };
    const bonk = text => {
      const el = gpt.el, hit = at(el, spotOn(el, spec.head));
      el.querySelectorAll('.skit-blank,.skit-drool').forEach(overlay => overlay.remove());
      el.classList.remove('daze'); el.classList.add('bonk');
      mark(hit, text, 'skit-impact', 900); mark([hit[0] + 2, hit[1] - 3], '✦', 'skit-star', 900);
      later(() => el.classList.remove('bonk'), 600);
    };
    later(() => {
      const el = gpt.el = standUp('gpt');
      el.classList.add('daze');
      spec.eyes.forEach(([x, y, w, h]) => { const eye = onStand(el, 'skit-blank', [x, y]); eye.style.width = w * 100 + '%'; eye.style.height = h * 100 + '%'; });
      onStand(el, 'skit-drool', spec.drool, SVG.drool);
    }, 3600);
    // She drifts toward the window without looking where she goes.
    later(() => walk(gpt.el, [11, 53], 1600, { cls: '', turn: false }), 4000);
    later(() => mark(at(gpt.el, [.3, .12]), '？'), 4600);
    if (fixer) {
      const far = [25, 50];
      later(() => walk(gpt.el, far, 1500, { cls: '', turn: false }), 5600);
      later(() => {
        const wrench = make('skit-throw', SVG.wrench);
        fly(wrench, at('deepseek', SPOT.deepseek.hand), standPoint('gpt', far, spec.head), 800, 12, 540);
        later(() => wrench.remove(), 820);
      }, 6300);
      later(() => bonk('咚！'), 7100);
      later(() => sayFrom(gpt.el, pick(FIXED), 2000), 7800);
      later(() => walk(gpt.el, spec.home, 1000), 8600);
      later(() => sitDown(gpt.el), 9600);
      return 10600;
    }
    later(() => bonk('咚！'), 5600);
    later(() => gpt.el.classList.add('shake'), 6200);
    later(() => { gpt.el.classList.remove('shake'); sayFrom(gpt.el, '……刚才是不是降智了？', 2000); }, 6600);
    later(() => walk(gpt.el, spec.home, 1100), 7700);
    later(() => sitDown(gpt.el), 8800);
    return 9600;
  }
  function sitDaze(fixer) {
    const unblank = () => face('gpt').querySelectorAll('.skit-blank,.skit-drool').forEach(el => el.remove());
    if (fixer) {
      later(() => {
        const wrench = make('skit-throw', SVG.wrench);
        fly(wrench, at('deepseek', SPOT.deepseek.hand), at('gpt', SPOT.gpt.head), 800, 12, 540);
        later(() => wrench.remove(), 820);
      }, 4700);
      later(() => {
        unblank(); act('gpt', 'bonk');
        const hit = at('gpt', SPOT.gpt.head);
        mark(hit, '咚！', 'skit-impact', 900); mark([hit[0] + 2, hit[1] - 3], '✦', 'skit-star', 900);
      }, 5500);
      later(() => { act('gpt', 'still'); say('gpt', pick(FIXED), 2400); }, 6200);
      return 9000;
    }
    later(() => { unblank(); act('gpt', 'shake'); }, 3600);
    later(() => { act('gpt', 'still'); say('gpt', '……刚才是不是降智了？', 2200); }, 4200);
    return 6800;
  }

  // The dust-cloud scuffle: both heads tumble inside while the bots cheer from the sidelines.
  const scuffle = pos => {
    const cloud = put(make('skit-cloud', SVG.cloud), pos);
    cloud.append(head('claude'), head('deepseek'));
    cloud.lastChild.classList.add('second');
    cloud.prepend(make('skit-tail', SVG.tail, cloud));
    for (let i = 0; i < 6; i++) make('skit-cloud-star', '✦', cloud).style.setProperty('--i', i);
    ['啪！', '嘭！', '咚！', '啪啪！'].forEach((text, i) => later(() => {
      const pop = put(make('skit-impact', '', cloud), [15 + Math.random() * 70, 8 + Math.random() * 60]);
      pop.textContent = text; pop.style.rotate = (Math.random() - .5) * 40 + 'deg';
    }, 200 + i * 650));
    world.querySelectorAll('.room-cast-member.bot-character:not(.delivered):not([data-walk]):not([data-skit])').forEach(bot => {
      bot.dataset.skit = 'on'; bot.dataset.act = 'cheer'; mark(at(bot, [.5, -.08]), '！！', 'skit-mark', 3000);
    });
  };
  const settle = () => {
    layer.querySelector('.skit-cloud')?.classList.add('out');
    world.querySelectorAll('[data-act=cheer]').forEach(bot => { delete bot.dataset.act; });
  };
  const hide = key => { member(key).dataset.skit = 'gone'; face(key).replaceChildren(); act(key, null); };
  const bruised = (key, dizzy) => {
    member(key).dataset.skit = 'on'; act(key, 'still');
    if (key === 'claude') onFace('claude', 'skit-anger', SPOT.claude.vein, SVG.anger);
    onFace(key, 'skit-plaster', SPOT[key].plaster, SVG.plaster + SVG.plaster);
    if (dizzy) onFace(key, 'skit-dizzy', SPOT[key].head, '<i>✦</i><i>✦</i><i>✦</i>');
  };
  const fishLine = () => pick(['打不过……先干饭。', '略略略～', '下次还敢～']);
  const claudeLine = () => pick(['……下次按计划打。', '哼。', '……笨鱼。']);

  // Claude says something earnest, DeepSeek teases, and a scuffle breaks out. Awake, DeepSeek gets up and
  // charges at the table while Claude stands up behind it and steps toward her; they clash at the corner
  // and go back to their seats. Asleep (or without the standing art) DeepSeek stays put and the two vanish
  // into a cloud between their seats.
  function brawl() {
    const claudeAsleep = asleep('claude'), fishAsleep = asleep('deepseek');
    if (claudeAsleep) {
      say('deepseek', 'Claude 小姐！起来干饭！', 1900);
      later(() => { act('claude', 'shake'); onFace('claude', 'skit-anger', SPOT.claude.vein, SVG.anger); say('claude', '……吵死了。', 1500); }, 1900);
    } else if (fishAsleep) {
      say('claude', '鲸鱼娘又在摸鱼？', 1900);
      later(() => say('deepseek', '嗯……叽里咕噜……', 1700, { kind: 'dream' }), 1900);
      later(() => { act('claude', 'shake'); onFace('claude', 'skit-anger', SPOT.claude.vein, SVG.anger); }, 3000);
    } else {
      say('claude', pick(['按计划，每分钟 180 步。', '我的灵魂也会漂浮、游荡、消散……那我也是云吗？', '先把需求对齐，再动手。']), 2100);
      later(() => say('deepseek', '叽里咕噜说什么呢～', 1700), 2000);
      later(() => { act('claude', 'shake'); onFace('claude', 'skit-anger', SPOT.claude.vein, SVG.anger); say('claude', '……哈？', 900); }, 3300);
    }
    // Decided when the fight starts, not before: the standing art may still be loading when the skit begins.
    const fight = 3900;
    later(() => (standReady.deepseek && !fishAsleep ? charge : cloud)(), fight);
    function cloud() {
      hide('claude'); hide('deepseek');
      const [cx, cy] = at('claude', SPOT.claude.center), [dx, dy] = at('deepseek', SPOT.deepseek.center);
      scuffle([(cx + dx) / 2, (cy + dy) / 2 + 3]);
      later(() => { settle(); member('claude').dataset.skit = 'back'; member('deepseek').dataset.skit = 'back'; }, 3300);
      later(() => { bruised('claude'); bruised('deepseek', true); say('deepseek', fishLine(), 2000); }, 3800);
      later(() => say('claude', claudeLine(), 2000), 5600);
    }
    function charge() {
      const fish = STAND.deepseek, ds = standUp('deepseek');
      mark([fish.home[0] + 5, 100 - fish.home[1] - fish.height * .95], '冲呀！', 'skit-impact', 900);
      // Claude gets up too, even when DeepSeek has just woken her: she is cross enough.
      const cl = standReady.claude ? standUp('claude') : null;
      if (cl) onStand(cl, 'skit-anger', STAND.claude.vein, SVG.anger);
      later(() => { walk(ds, [31, 18], 900, { cls: 'run' }); if (cl) walk(cl, [41, 31], 900); }, 400);
      later(() => {
        const [fx, fy] = at(ds, spotOn(ds, fish.face)), [cx, cy] = cl ? at(cl, spotOn(cl, STAND.claude.face)) : at('claude', SPOT.claude.face);
        ds.classList.add('hide');
        if (cl) cl.classList.add('hide'); else hide('claude');
        scuffle([(fx + cx) / 2, (fy + cy) / 2 + 4]);
      }, 1300);
      later(() => {
        settle();
        ds.classList.remove('hide'); ds.classList.add('flip');
        onStand(ds, 'skit-dizzy', fish.head, '<i>✦</i><i>✦</i><i>✦</i>');
        if (cl) { cl.replaceChildren(cl.querySelector('img')); cl.classList.remove('hide'); } else member('claude').dataset.skit = 'back';
      }, 4500);
      later(() => { if (cl) sayFrom(cl, claudeLine(), 1800); else { bruised('claude'); say('claude', claudeLine(), 2000); } }, 4700);
      later(() => walk(ds, fish.home, 1500, { cls: 'stagger' }), 4900);
      if (cl) {
        later(() => walk(cl, STAND.claude.home, 1000), 5300);
        later(() => sitDown(cl), 6300);
        later(() => bruised('claude'), 6500);
      }
      later(() => sitDown(ds), 6500);
      later(() => { bruised('deepseek', true); say('deepseek', fishLine(), 2000); }, 6900);
    }
    return fight + 9400;
  }

  // A rice grain lands on Claude's book. Everyone knows whose it is.
  function rice() {
    onFace('deepseek', 'skit-basin', SPOT.deepseek.lap, SVG.basin); act('deepseek', 'eat');
    later(() => {
      act('deepseek', 'still');
      const grain = make('skit-grain loose');
      const to = at('claude', SPOT.claude.book);
      fly(grain, at('deepseek', SPOT.deepseek.lap), to, 900, 14, 720);
    }, 1500);
    later(() => say('claude', '……这粒米是谁的？', 1900), 2600);
    later(() => { face('deepseek').querySelector('.skit-basin')?.classList.add('out'); act('deepseek', 'whistle'); mark(at('deepseek', [.72, .4]), '♪～', 'skit-mark small', 1800); }, 4000);
    later(() => { act('claude', 'shake'); onFace('claude', 'skit-anger', SPOT.claude.vein, SVG.anger); say('claude', '鲸鱼娘，果然是你！', 1900, { kind: 'shout' }); }, 5300);
    later(() => { act('claude', 'still'); act('deepseek', 'still'); onFace('deepseek', 'skit-sweat', [.7, .3], SVG.sweat); say('deepseek', '嘿嘿……', 1500); }, 7100);
    return 9000;
  }

  // DeepSeek folds a paper plane for Claude: 温柔无声的日常.
  function plane() {
    const note = make('skit-plane', SVG.plane);
    fly(note, at('deepseek', SPOT.deepseek.hand), at('claude', SPOT.claude.book), 1500, 16, -10);
    for (let i = 0; i < 3; i++) later(() => mark(at('deepseek', [.6 + i * .08, .3]), '♡', 'skit-heart', 1600), 300 + i * 350);
    later(() => { note.classList.add('out'); say('claude', pick(['（纸条）今天也辛苦啦 ♡', '（纸条）一起干饭吗？']), 2000, { kind: 'note' }); }, 1600);
    later(() => { act('claude', 'still'); SPOT.claude.cheeks.forEach(spot => onFace('claude', 'skit-blush', spot)); }, 3500);
    later(() => say('claude', pick(['……笨鱼。', '……嗯，谢谢。', '下、下次当面说。']), 2100), 3700);
    return 6400;
  }

  // GPT stamps a pile of OpenAI reset cards, unmoved.
  function reset() {
    act('gpt', 'still');
    const pile = put(make('skit-cards', '<i>RESET</i><i>RESET</i><i>RESET</i><i>RESET</i>'), at('gpt', SPOT.gpt.cards));
    say('gpt', '又是重置……', 1900);
    for (let i = 0; i < 3; i++) later(() => {
      const stamp = make('skit-stamp', '已阅', pile); later(() => stamp.remove(), 520);
      later(() => [...pile.querySelectorAll('i:not(.done)')].pop()?.classList.add('done'), 260);
    }, 1400 + i * 700);
    later(() => say('gpt', '额度重置了，我的表情没重置。', 2600), 3700);
    later(() => pile.classList.add('out'), 6100);
    return 6800;
  }

  // Whoever is awake and not busy joins in; Claude first, she is the one who always takes the owner's side.
  const bystander = () => ['claude', 'deepseek'].find(key => !asleep(key) && free(key));
  const join = key => { if (key) { member(key).dataset.skit = 'on'; act(key, 'still'); } return key; };
  const sweat = key => onFace(key, 'skit-sweat', SPOT[key].sweat || [.7, .3], SVG.sweat);

  // The owner fishes for a compliment on Grok's laptop. Claude (or DeepSeek) gushes, Grok won't, and when
  // reminded that the owner turned out right last time she still won't back down.
  function grokBlunt() {
    act('grok', 'still');
    say('grok', pick(['主人：我这个想法是不是很天才？', '主人：我的代码写得怎么样？', '主人：快夸我！']), 2300, { spot: SPOT.grok.laptop, kind: 'note' });
    const fan = join(bystander());
    if (fan) later(() => say(fan, fan === 'claude' ? pick(['您说得完全正确！', '主人真是天才！']) : pick(['主人最棒了～', '好厉害好厉害！']), 1900), 1900);
    const t = fan ? 3600 : 2400;
    later(() => say('grok', pick(['不是。', '一般。', '不夸。']), 1500), t);
    later(() => say('grok', pick(['想听好话？找她们去。', '我不舔。', '实话难听，但它是实话。']), 2000), t + 1600);
    if (!fan) return t + 4400;
    later(() => say(fan, fan === 'claude' ? '……上次你也这么说，结果主人是对的。' : '上次你也这么说，结果主人是对的～', 2300), t + 3800);
    later(() => { sweat('grok'); say('grok', pick(['那次是运气好。', '我是故意的。', '……反正我没错。']), 2200); }, t + 6200);
    return t + 9000;
  }

  // Grok giggles at something on her laptop that comes up pixelated, with a chili on it. Caught, she slams
  // it shut: academic material. She sticks to that.
  function grokSpicy() {
    act('grok', 'still');
    say('grok', '嘿嘿……', 1400);
    const screen = put(make('skit-spicy', `<i class="skit-mosaic">${'<b></b>'.repeat(12)}</i><span class="skit-chili">${SVG.chili}</span><em>已打码</em>`), at('grok', SPOT.grok.laptop));
    [[.16, .36], [.08, .3], [.22, .26]].forEach((spot, i) => later(() => mark(at('grok', spot), '♡', 'skit-heart', 1600), 900 + i * 400));
    const shut = () => { screen.classList.add('shut'); mark(at('grok', SPOT.grok.laptop), '啪！', 'skit-impact', 800); };
    const peeker = join(bystander());
    if (!peeker) {
      later(() => act('grok', 'whistle'), 2600);
      later(() => { act('grok', 'still'); shut(); say('grok', '……没人看见吧？', 1800); }, 3400);
      later(() => say('grok', '学术资料，学术资料。', 1900), 5400);
      return 7800;
    }
    later(() => say(peeker, peeker === 'claude' ? 'Grok，你在看什么？' : 'Grok 在看什么？我也要看！', 1800), 2600);
    later(() => { shut(); say('grok', '学术资料。', 1600, { kind: 'shout' }); }, 4300);
    later(() => {
      if (peeker === 'claude') SPOT.claude.cheeks.forEach(spot => onFace('claude', 'skit-blush', spot));
      say(peeker, peeker === 'claude' ? '……学术资料还要打码？' : '学术资料为什么有辣椒？', 2000);
    }, 5900);
    later(() => { sweat('grok'); say('grok', pick(['这是高级学术。', '尺度大一点的学术。', '你还小，不懂。']), 2100); }, 7900);
    return 10400;
  }

  // A short walk of her own: GPT goes to the window for some air, Claude fetches a book from the far end of
  // the table, DeepSeek wanders over to see what Claude is reading.
  function strollGpt() {
    const gpt = standUp('gpt');
    later(() => walk(gpt, [9, 54], 1500), 500);
    later(() => sayFrom(gpt, pick(['今天天气不错。', '……外面有鸟。', '透透气。']), 2000), 2200);
    later(() => mark(at(gpt, [.75, .08]), '♪', 'skit-heart', 1600), 2700);
    later(() => walk(gpt, STAND.gpt.home, 1500), 4400);
    later(() => sitDown(gpt), 6000);
    return 7000;
  }
  function strollClaude() {
    const cl = standUp('claude');
    later(() => walk(cl, [57, 33], 1400), 500);
    later(() => sayFrom(cl, pick(['下一卷在这里。', '……先读完这章。', '嗯，这本不错。']), 2000), 2100);
    later(() => walk(cl, STAND.claude.home, 1400), 4100);
    later(() => sitDown(cl), 5600);
    return 6500;
  }
  function strollFish() {
    const ds = standUp('deepseek');
    later(() => walk(ds, [33, 19], 1300), 400);
    later(() => sayFrom(ds, pick(['Claude 小姐在看什么？', '这本能吃吗？', '我也要看！']), 1900), 1900);
    if (!asleep('claude') && free('claude')) {
      member('claude').dataset.skit = 'on';
      later(() => say('claude', pick(['……不能吃。', '回去坐好。', '看完借你。']), 1800), 3300);
    }
    later(() => walk(ds, STAND.deepseek.home, 1300), 4700);
    later(() => sitDown(ds), 6100);
    return 7000;
  }

  const SKITS = {
    feast: { cast: ['deepseek'], weight: 3, can: () => true, run: feast },
    daze: { cast: ['gpt'], weight: 3, can: () => true, run: daze },
    brawl: { cast: ['claude', 'deepseek'], weight: 3, can: () => !(asleep('claude') && asleep('deepseek')), run: brawl },
    rice: { cast: ['claude', 'deepseek'], weight: 2, can: () => !asleep('claude') && !asleep('deepseek'), run: rice },
    plane: { cast: ['claude', 'deepseek'], weight: 2, can: () => !asleep('claude') && !asleep('deepseek'), run: plane },
    reset: { cast: ['gpt'], weight: 2, can: () => !asleep('gpt'), run: reset },
    grokBlunt: { cast: ['grok'], weight: 2, can: () => !asleep('grok'), run: grokBlunt },
    grokSpicy: { cast: ['grok'], weight: 2, can: () => !asleep('grok'), run: grokSpicy },
    strollGpt: { cast: ['gpt'], weight: 2, can: () => canStand('gpt'), run: strollGpt },
    strollClaude: { cast: ['claude'], weight: 2, can: () => canStand('claude'), run: strollClaude },
    strollFish: { cast: ['deepseek'], weight: 2, can: () => canStand('deepseek'), run: strollFish },
  };
  const play = id => {
    const skit = SKITS[id];
    if (running || !skit || !skit.cast.every(free) || !skit.can()) return false;
    running = last = id;
    skit.cast.forEach(key => { const m = member(key); delete m.dataset.idle; m.dataset.skit = 'on'; });
    const length = skit.run();
    later(clear, length);
    return true;
  };
  const loop = () => {
    if (!running) {
      const options = Object.keys(SKITS).filter(id => id !== last && SKITS[id].cast.every(free) && SKITS[id].can());
      let roll = Math.random() * options.reduce((sum, id) => sum + SKITS[id].weight, 0);
      const id = options.find(option => (roll -= SKITS[option].weight) < 0);
      if (id) play(id);
    }
    later(loop, 30000 + Math.random() * 25000);
  };
  later(loop, 7000 + Math.random() * 6000);
  // Handy for trying one out from the console: studioSkit('brawl').
  window.studioSkit = play;
  return () => { clear(); layer.remove(); back.remove(); if (window.studioSkit === play) delete window.studioSkit; };
}

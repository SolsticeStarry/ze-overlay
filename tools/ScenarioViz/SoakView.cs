using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using ZeOverlay.Shared;
using ZeOverlay.Tests.Scenarios;

namespace ZeOverlay.Tools.ScenarioViz;

/// <summary>
/// 把 <see cref="RandomizedServerSimulation"/> 的连续随机仿真渲染成一个可播放的页面：
/// 左边模仿真机截图的游戏 HUD（EXG 绿底描边 / FYS 标题+三列+彩色下划线），右边是 ZeOverlay 的实际输出，
/// 下面按时间滚动事件日志与事件时间轴；支持播放/暂停/倍速/拖动。
/// </summary>
public static class SoakView
{
    public static string Render(SoakRun exg, SoakRun fys)
    {
        var payload = new
        {
            exg = BuildPayload(exg),
            fys = BuildPayload(fys),
        };

        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        return Template.Replace("__DATA__", json, StringComparison.Ordinal);
    }

    private static object BuildPayload(SoakRun run) => new
    {
        server = run.Kind == ServerKind.Fys ? "fys" : "exg",
        seed = run.Seed,
        seconds = run.Seconds,
        violations = run.Violations,
        stats = new
        {
            pickUps = run.PickUps,
            losses = run.Losses,
            burns = run.Burns,
            reorders = run.Reorders,
            playerChanges = run.PlayerChanges,
            misses = run.Misses,
            sameNamePickUps = run.SameNamePickUps,
            maxSameName = run.MaxSameName,
            averageCount = run.AverageCount,
            minCount = run.MinCount,
            maxCount = run.MaxCount,
            averageArtifacts = run.AverageArtifacts,
            minArtifacts = run.MinArtifacts,
            maxArtifacts = run.MaxArtifacts,
        },
        frames = run.Frames.Select(f => new
        {
            t = f.At,
            scene = new { hue = f.Scene.Hue, sat = f.Scene.Sat, light = f.Scene.Light },
            rows = f.Rows.Select(r => new
            {
                name = r.Name,
                index = r.Index,
                player = r.Player,
                cool = r.State == ArtifactState.Cooling,
                cd = r.CooldownSeconds,
                r = r.UsesRemaining,
                total = r.UsesTotal,
                text = r.StatusText,
            }),
            overlay = f.Overlay.Select(e => new
            {
                name = e.ArtifactName,
                player = e.PlayerName,
                index = e.ServerIndex,
                page = (int)e.Page,
                slot = e.Slot,
                state = e.State == ArtifactState.Cooling ? "cool" : e.State == ArtifactState.Ready ? "ready" : "unknown",
                cd = e.CooldownSeconds,
                r = e.UsesRemaining,
                total = e.UsesTotal,
                src = e.Source == EntrySource.Live ? "实" : "推",
            }),
            events = f.Events.Select(ev => new { kind = ev.Kind, text = ev.Text }),
        }),
    };

    private const string Template = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ZeOverlay · 双服连续随机仿真</title>
<style>
:root{--bg:#0d1117;--panel:#161d26;--panel2:#1b2430;--line:#2a3947;--txt:#dfe7f0;--dim:#8296aa;
      --ready:#3fb950;--cool:#e3a008;--add:#3fb950;--del:#f85149;--accent:#58a6ff;}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.55 "Segoe UI","Microsoft YaHei",sans-serif}
a{color:var(--accent)}
header{padding:16px 24px;border-bottom:1px solid var(--line);background:linear-gradient(180deg,#141b24,#0d1117)}
header h1{margin:0 0 3px;font-size:19px}
header p{margin:0;color:var(--dim);font-size:12.5px}
.wrap{max-width:1320px;margin:0 auto;padding:16px 24px 40px}
.toolbar{display:flex;flex-wrap:wrap;gap:10px;align-items:center;background:var(--panel);border:1px solid var(--line);
         border-radius:10px;padding:10px 12px;margin-bottom:14px}
.tabs{display:flex;gap:6px;margin-right:8px}
.tabs button{background:#1d2836;border:1px solid var(--line);color:var(--txt);border-radius:8px;padding:6px 14px;cursor:pointer;font-size:13px}
.tabs button.active{background:#22384f;border-color:var(--accent);font-weight:600}
.tbtn{background:#22384f;border:1px solid var(--accent);color:#fff;border-radius:8px;padding:6px 14px;cursor:pointer;font-size:13px;min-width:74px}
input[type=range]{flex:1;min-width:180px;accent-color:var(--accent)}
.clock{font-family:Consolas,monospace;color:var(--accent);min-width:104px;text-align:right;font-weight:600}
select{background:#1d2836;color:var(--txt);border:1px solid var(--line);border-radius:6px;padding:5px 8px}
.stage{display:grid;grid-template-columns:1.12fr .88fr;gap:14px;align-items:start}
@media(max-width:980px){.stage{grid-template-columns:1fr}}
.panel{background:var(--panel);border:1px solid var(--line);border-radius:12px;overflow:hidden}
.panel h3{margin:0;padding:9px 12px;font-size:11.5px;letter-spacing:.1em;text-transform:uppercase;color:var(--dim);
          background:var(--panel2);border-bottom:1px solid var(--line)}
.game{min-height:560px;padding:14px 16px;position:relative;overflow:auto;
  background-color:#2f3a1e;transition:background-color .25s linear;
  background-image:
    radial-gradient(ellipse at 30% 14%, rgba(255,255,255,.12), transparent 55%),
    radial-gradient(ellipse at 76% 80%, rgba(0,0,0,.22), transparent 62%),
    repeating-linear-gradient(92deg, rgba(255,255,255,.03) 0 2px, transparent 2px 6px);}
/* ---- EXG：绿底描边白字（底色随场景随机漂移） ---- */
.game.exg .grow{height:30px;line-height:30px;font-size:17px;color:#f2f2ec;white-space:nowrap;
  text-shadow:0 0 2px #000,1px 1px 0 #000,-1px -1px 0 #000,1px -1px 0 #000,-1px 1px 0 #000,0 0 6px rgba(0,0,0,.6)}
.gname{font-weight:500}.gstate{color:#fff}.guses{color:#fff;font-weight:600}
.gplayer{color:#f6f6f0;opacity:.96}
.gempty{color:#dfe7d8;font-style:italic}
/* ---- FYS：标题 + 三列 + 彩色下划线（底色随场景随机漂移） ---- */
.ftitle{color:#ffb020;font-weight:700;font-size:15px;letter-spacing:.02em;border-bottom:4px solid #ffb020;
        padding-bottom:3px;margin-bottom:8px;display:flex;justify-content:space-between;align-items:flex-end;
        text-shadow:0 1px 3px rgba(0,0,0,.95)}
.ftitle .fsub{font-size:10.5px;color:#ffca66;font-weight:600;letter-spacing:.08em;text-shadow:0 1px 3px rgba(0,0,0,.95)}
.frow{margin-bottom:5px}
.fline{display:grid;grid-template-columns:104px 1fr 88px;align-items:center;height:22px;font-size:13px}
.fname{color:#ffffff;font-weight:600;text-shadow:0 1px 3px rgba(0,0,0,.95)}
.findex{color:#ffffff;font-weight:600;text-shadow:0 1px 3px rgba(0,0,0,.95)}
.fplayer{color:#ffd24a;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;text-shadow:0 1px 3px rgba(0,0,0,.95)}
.fstatus{text-align:right;color:#ffffff;font-weight:700;font-family:Consolas,monospace;text-shadow:0 1px 3px rgba(0,0,0,.95)}
.fbar{height:3px;border-radius:2px;opacity:.95}
.rowflash{animation:flash .7s ease-out}
@keyframes flash{0%{background:rgba(88,166,255,.42)}100%{background:transparent}}
/* ---- overlay ---- */
.ov{padding:12px;min-height:560px}
.orow{display:grid;grid-template-columns:70px minmax(88px,1fr) minmax(64px,.9fr) auto auto 24px;gap:8px;align-items:center;
      padding:5px 8px;border-bottom:1px solid #1f2b38}
.orow:last-child{border-bottom:0}
.oslot{font-family:Consolas,monospace;color:var(--dim);font-size:11.5px}
.oname{font-weight:600}.oidx{color:var(--accent);margin-left:3px}
.oplayer{color:#ffd24a;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.ouses{color:#c9a7ff;font-family:Consolas,monospace;font-size:12px}
.osrc{color:var(--dim);font-size:11px;text-align:center}
.badge{display:inline-block;padding:1px 7px;border-radius:99px;font-size:11.5px}
.badge.ready{background:rgba(63,185,80,.13);color:var(--ready);border:1px solid rgba(63,185,80,.4)}
.badge.cool{background:rgba(227,160,8,.13);color:var(--cool);border:1px solid rgba(227,160,8,.45)}
.badge.unknown{background:#222c36;color:var(--dim);border:1px solid #2f3d4b}
.oempty{color:var(--dim);font-style:italic}
/* ---- events ---- */
.lower{margin-top:14px;display:grid;grid-template-columns:1fr;gap:14px}
.strip{position:relative;height:34px;background:var(--panel2);border:1px solid var(--line);border-radius:10px;margin-bottom:8px}
.strip .dot{position:absolute;top:13px;width:8px;height:8px;border-radius:50%;transform:translateX(-4px);cursor:pointer}
.strip .cursor{position:absolute;top:0;bottom:0;width:2px;background:var(--accent);transform:translateX(-1px)}
.evlog{max-height:230px;overflow:auto;font-family:Consolas,"Microsoft YaHei",monospace;font-size:12.5px}
.ev{display:grid;grid-template-columns:60px 46px 1fr;gap:8px;padding:3px 6px;border-radius:5px}
.ev.now{background:#1c2a3a}
.evt{color:var(--dim)}
.evk{font-weight:700;text-align:center;border-radius:4px;font-size:11px;line-height:18px}
.k-拾取{background:rgba(63,185,80,.18);color:var(--add)}
.k-使用{background:rgba(227,160,8,.18);color:var(--cool)}
.k-丢失{background:rgba(248,81,73,.18);color:var(--del)}
.k-换行{background:rgba(88,166,255,.18);color:var(--accent)}
.k-换人{background:rgba(201,167,255,.18);color:#c9a7ff}
.k-漏读{background:#2a3542;color:var(--dim)}
.stat{display:flex;flex-wrap:wrap;gap:14px;font-size:12.5px;color:var(--dim);padding:0 2px}
.stat b{color:var(--txt)}
.ok{color:var(--ready)}.bad{color:var(--del)}
footer{padding:14px 24px;color:var(--dim);font-size:12px;border-top:1px solid var(--line);margin-top:18px}
</style>
</head>
<body>
<header>
  <h1>ZeOverlay · 双服连续随机仿真</h1>
  <p>一段时间内、随机时刻混入「拾取新增 / 使用冷却 / 丢失 / 换行 / 换人 / 倒计时 / OCR 漏读」，逐秒喂真实管线（S4→S6）。
     左边模仿真机截图的游戏 HUD，右边是叠加输出。同一个种子可复现。</p>
</header>
<div class="wrap">
  <div class="toolbar">
    <div class="tabs">
      <button id="tab-exg" class="active">EXG · 括号</button>
      <button id="tab-fys">FYS · 连写</button>
    </div>
    <button class="tbtn" id="play">▶ 播放</button>
    <select id="speed">
      <option value="2" selected>2×</option>
      <option value="1">1×</option>
      <option value="0.5">0.5×</option>
      <option value="4">4×</option>
    </select>
    <input type="range" id="scrub" min="0" max="0" value="0">
    <span class="clock" id="clock">t=0s</span>
  </div>

  <div class="stat" id="stats"></div>

  <div class="stage" style="margin-top:10px">
    <div class="panel">
      <h3>游戏画面（仿真 HUD）</h3>
      <div class="game exg" id="game"></div>
    </div>
    <div class="panel">
      <h3>ZeOverlay 输出（S5 跟踪 → S6 展示）</h3>
      <div class="ov" id="overlay"></div>
    </div>
  </div>

  <div class="lower">
    <div class="panel">
      <h3>随机事件时间轴（点圆点跳转）</h3>
      <div style="padding:10px 12px">
        <div class="strip" id="strip"></div>
        <div class="evlog" id="events"></div>
      </div>
    </div>
  </div>
</div>
<footer>由 <code>tools/ScenarioViz</code> 生成 · 与 <code>tests/ZeOverlay.Tests/Scenarios/RandomizedServerSimulation.cs</code> 共用同一份随机流与不变量校验。</footer>
<script>
const DATA = __DATA__;
const PALETTE = ['#e05252','#4ac8e0','#8ad04a','#f0a020','#c060d0','#40b0e0'];
const $ = id => document.getElementById(id);
const esc = s => String(s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const cur = { server:'exg', i:0, playing:false, speed:2, loop:true, timer:null };

function frame(){ return DATA[cur.server].frames[cur.i]; }
function hasEvent(f, name){ return f.events.some(e => e.text.indexOf(name) >= 0); }

function renderExg(f){
  const g = $('game');
  if(!f.rows.length){ g.innerHTML = '<div class="gempty">（列表为空）</div>'; return; }
  g.innerHTML = f.rows.map(r => {
    const flash = hasEvent(f, r.name) ? ' rowflash' : '';
    const uses = (r.r!=null && r.total!=null) ? `<span class="guses">${r.r}/${r.total}</span>` : '';
    return `<div class="grow${flash}"><span class="gname">${esc(r.name)}</span> `
         + `<span class="gstate">[${esc(r.text)}]</span>${uses} `
         + `<span class="gplayer">${esc(r.player)}</span></div>`;
  }).join('');
}

function renderFys(f){
  const g = $('game');
  let html = '<div class="ftitle">地图神器 · 人类<span class="fsub">SlotMode&nbsp;&nbsp;BounsMode</span></div>';
  if(!f.rows.length){ html += '<div class="gempty">（列表为空）</div>'; }
  f.rows.forEach((r, i) => {
    const flash = hasEvent(f, r.name) ? ' rowflash' : '';
    html += `<div class="frow${flash}"><div class="fline">`
         + `<span class="fname">${esc(r.name)}${r.index!=null?`<span class="findex">${r.index}</span>`:''}</span>`
         + `<span class="fplayer">${esc(r.player)}</span>`
         + `<span class="fstatus">${esc(r.text)}</span></div>`
         + `<div class="fbar" style="background:${PALETTE[i % PALETTE.length]}"></div></div>`;
  });
  g.innerHTML = html;
}

function renderOverlay(f){
  const el = $('overlay');
  if(!f.overlay.length){ el.innerHTML = '<div class="oempty">（无条目）</div>'; return; }
  el.innerHTML = f.overlay.map(e => {
    const idx = e.index!=null ? `<span class="oidx">#${e.index}</span>` : '';
    const badge = e.state==='cool' ? `<span class="badge cool">${e.cd}s</span>`
                : e.state==='ready' ? '<span class="badge ready">就绪</span>'
                : '<span class="badge unknown">?</span>';
    const uses = (e.r!=null && e.total!=null) ? `<span class="ouses">⚑ ${e.r}/${e.total}</span>` : '<span></span>';
    return `<div class="orow"><span class="oslot">P${e.page}#${e.slot}</span>`
         + `<span class="oname">${esc(e.name)}${idx}</span>`
         + `<span class="oplayer">${esc(e.player||'')}</span>${badge}${uses}`
         + `<span class="osrc">${e.src}</span></div>`;
  }).join('');
}

function renderEvents(){
  const d = DATA[cur.server];
  let list = [];
  for(let k=0;k<=cur.i;k++) d.frames[k].events.forEach(ev => list.push({t:d.frames[k].t, ...ev}));
  list = list.slice(-80).reverse();
  const nowT = d.frames[cur.i].t;
  $('events').innerHTML = list.length ? list.map(ev =>
    `<div class="ev${ev.t===nowT?' now':''}"><span class="evt">t=${ev.t}s</span>`
    + `<span class="evk k-${ev.kind}">${ev.kind}</span><span>${esc(ev.text)}</span></div>`).join('')
    : '<div class="ev">（暂无事件）</div>';
}

function renderStrip(){
  const d = DATA[cur.server];
  const strip = $('strip');
  const dots = [];
  d.frames.forEach(fr => fr.events.forEach(ev => {
    const left = (fr.t / d.seconds) * 100;
    dots.push(`<div class="dot" data-t="${fr.t}" title="t=${fr.t}s ${esc(ev.text)}" style="left:${left}%;background:${colorOf(ev.kind)}"></div>`);
  }));
  strip.innerHTML = dots.join('') + '<div class="cursor" id="cursor"></div>';
  strip.querySelectorAll('.dot').forEach(dot => dot.addEventListener('click', () => { cur.i = +dot.dataset.t; render(); }));
}

function colorOf(kind){
  return {'拾取':'#3fb950','使用':'#e3a008','丢失':'#f85149','换行':'#58a6ff','换人':'#c9a7ff','漏读':'#8296aa'}[kind] || '#8296aa';
}

function applyScene(f){
  const s = f.scene || {hue:78, sat:34, light:20};
  $('game').style.backgroundColor = `hsl(${s.hue}, ${s.sat}%, ${s.light}%)`;
}

function render(){
  const d = DATA[cur.server];
  cur.i = Math.max(0, Math.min(d.frames.length - 1, cur.i));
  const f = d.frames[cur.i];
  $('game').className = 'game ' + cur.server;
  applyScene(f);
  if(cur.server === 'exg') renderExg(f); else renderFys(f);
  renderOverlay(f);
  renderEvents();
  $('scrub').max = d.frames.length - 1;
  $('scrub').value = cur.i;
  $('clock').textContent = `t=${f.t}s / ${d.seconds}s`;
  const c = $('cursor');
  if(c) c.style.left = (f.t / d.seconds) * 100 + '%';
}

function renderStats(){
  const d = DATA[cur.server];
  const v = d.violations.length;
  const s = d.stats;
  $('stats').innerHTML =
    `<span>服务器 <b>${cur.server.toUpperCase()}</b> · 种子 <b>${d.seed}</b> · 时长 <b>${d.seconds}s</b></span>`
    + `<span>不变量：<b class="${v?'bad':'ok'}">${v?('违规 '+v):'全部通过'}</b></span>`
    + `<span>拾取 <b>${s.pickUps}</b> · 使用 <b>${s.burns}</b> · 丢失 <b>${s.losses}</b> · 换行 <b>${s.reorders}</b> · 换人 <b>${s.playerChanges}</b> · 漏读 <b>${s.misses}</b></span>`
    + `<span>同类多神器：拾取 <b>${s.sameNamePickUps}</b> · 同名峰值 <b>${s.maxSameName}</b></span>`
    + `<span>神器总数：平均 <b>${s.averageArtifacts}</b>（${s.minArtifacts}–${s.maxArtifacts}）· 可见行：平均 <b>${s.averageCount}</b>（${s.minCount}–${s.maxCount}）</span>`;
  if(v){ $('stats').innerHTML += `<span class="bad">${esc(d.violations.slice(0,3).join(' | '))}</span>`; }
}

function setServer(s){
  pause();
  cur.server = s;
  cur.i = 0;
  $('tab-exg').classList.toggle('active', s === 'exg');
  $('tab-fys').classList.toggle('active', s === 'fys');
  renderStrip();
  renderStats();
  render();
}

function play(){
  if(cur.playing) return;
  cur.playing = true;
  $('play').textContent = '⏸ 暂停';
  schedule();
}
function pause(){
  cur.playing = false;
  $('play').textContent = '▶ 播放';
  if(cur.timer){ clearTimeout(cur.timer); cur.timer = null; }
}
function schedule(){
  if(!cur.playing) return;
  cur.timer = setTimeout(() => {
    const d = DATA[cur.server];
    if(cur.i >= d.frames.length - 1){
      if(cur.loop){ cur.i = 0; render(); schedule(); }
      else { pause(); }
      return;
    }
    cur.i++; render(); schedule();
  }, 1000 / cur.speed);
}

$('tab-exg').addEventListener('click', () => setServer('exg'));
$('tab-fys').addEventListener('click', () => setServer('fys'));
$('play').addEventListener('click', () => cur.playing ? pause() : play());
$('speed').addEventListener('change', e => { cur.speed = +e.target.value; if(cur.playing){ pause(); play(); } });
$('scrub').addEventListener('input', e => { cur.i = +e.target.value; render(); });

const initial = new URLSearchParams(location.search).get('server') === 'fys' ? 'fys' : 'exg';
setServer(initial);
play();
</script>
</body>
</html>
""";
}

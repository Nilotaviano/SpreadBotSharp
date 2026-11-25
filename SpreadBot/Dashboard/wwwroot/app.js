// SpreadBot dashboard. No framework, no build step: elements are built with h()/s(), never from HTML strings, so nothing the bot
// reports (market names, log lines) can inject markup.

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

function h(tag, props, ...children) {
  const element = document.createElement(tag);
  applyProps(element, props);
  append(element, children);
  return element;
}

function s(tag, props, ...children) {
  const element = document.createElementNS('http://www.w3.org/2000/svg', tag);
  applyProps(element, props, true);
  append(element, children);
  return element;
}

function applyProps(element, props, svg) {
  for (const [key, value] of Object.entries(props || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') element.setAttribute('class', value);
    else if (key === 'text') element.textContent = value;
    else if (key.startsWith('on')) element.addEventListener(key.slice(2), value);
    else if (key === 'dataset') Object.assign(element.dataset, value);
    else if (key === 'style' && typeof value === 'object') Object.assign(element.style, value);
    else if (svg || key.includes('-') || key === 'role' || key === 'title' || key === 'href' || key === 'type') element.setAttribute(key, value === true ? '' : value);
    else element[key] = value;
  }
}

function append(element, children) {
  for (const child of children.flat(Infinity)) {
    if (child === undefined || child === null || child === false) continue;
    element.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
}

function replace(element, ...children) {
  element.replaceChildren();
  append(element, children);
}

// ---------- Formatting ----------

const fmt = {
  amount(value, digits = 8) {
    if (value === null || value === undefined || Number.isNaN(Number(value))) return '—';
    const n = Number(value);
    const fixed = Math.abs(n) >= 1000 ? n.toLocaleString('en-US', { maximumFractionDigits: 2 }) : n.toFixed(digits).replace(/\.?0+$/, '');
    return fixed === '-0' ? '0' : fixed;
  },
  signed(value, digits = 8) {
    if (value === null || value === undefined) return '—';
    return (value > 0 ? '+' : '') + fmt.amount(value, digits);
  },
  pct(value, digits = 2) {
    if (value === null || value === undefined) return '—';
    return `${Number(value).toFixed(digits)}%`;
  },
  compact(value) {
    if (value === null || value === undefined) return '—';
    return Number(value).toLocaleString('en-US', { notation: 'compact', maximumFractionDigits: 2 });
  },
  ago(date) {
    if (!date) return 'never';
    const seconds = Math.max(0, (Date.now() - new Date(date)) / 1000);
    if (seconds < 60) return `${Math.round(seconds)} s ago`;
    if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
    if (seconds < 172800) return `${(seconds / 3600).toFixed(1)} h ago`;
    return `${Math.round(seconds / 86400)} days ago`;
  },
  duration(ms) {
    const minutes = Math.max(0, ms / 60000);
    if (minutes < 1) return `${Math.round(minutes * 60)} s`;
    if (minutes < 90) return `${Math.round(minutes)} min`;
    if (minutes < 2880) return `${(minutes / 60).toFixed(1)} h`;
    return `${(minutes / 1440).toFixed(1)} days`;
  },
  time(date) { return new Date(date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }); },
  day(date) { return new Date(date).toLocaleDateString([], { month: 'short', day: 'numeric' }); },
  datetime(date) { return new Date(date).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }); },
};

const tone = value => value > 0 ? 'up' : value < 0 ? 'down' : '';

// ---------- State ----------

const store = {
  token: localStorage.getItem('spreadbot.token'),
  state: JSON.parse(localStorage.getItem('spreadbot.state') || 'null'),
  stateIsCached: true,
  overviewHistory: null,
  history: null,
  markets: null,
  activity: [],
  logs: [],
  orders: [],
  push: null,
  connected: false,
  tab: 'overview',
  filters: { botsSearch: '', botsState: '', botsSort: 'market', marketsSearch: '', marketsEligible: false, marketsSort: 'score', marketsAsc: false, activityKind: '', logsLevel: '', logsSearch: '', historyDays: 30, ordersMarket: '' },
};

// ---------- API ----------

class Unauthorized extends Error {}

async function api(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: { Authorization: `Bearer ${store.token}`, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (response.status === 401) {
    showLogin('The token was refused.');
    throw new Unauthorized();
  }
  const data = response.headers.get('content-type')?.includes('json') ? await response.json() : null;
  if (!response.ok) throw new Error(data?.error || `${response.status} ${response.statusText}`);
  return data;
}

// ---------- Live updates (server-sent events over fetch, which can send the token) ----------

let streamAbort;
let retryDelay = 1000;

async function connect() {
  streamAbort?.abort();
  const abort = streamAbort = new AbortController();

  while (!abort.signal.aborted) {
    try {
      const response = await fetch('/api/stream', { headers: { Authorization: `Bearer ${store.token}` }, signal: abort.signal, cache: 'no-store' });
      if (response.status === 401) { showLogin('The token was refused.'); return; }
      if (!response.ok || !response.body) throw new Error(`${response.status}`);

      setConnected(true);
      retryDelay = 1000;
      const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
      let buffer = '';
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += value;
        let boundary;
        while ((boundary = buffer.indexOf('\n\n')) >= 0) {
          const block = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          dispatch(block);
        }
      }
    } catch (error) {
      if (abort.signal.aborted) return;
    }
    setConnected(false);
    await new Promise(resolve => setTimeout(resolve, retryDelay));
    retryDelay = Math.min(retryDelay * 2, 30000);
  }
}

function dispatch(block) {
  let event = 'message';
  const data = [];
  for (const line of block.split('\n')) {
    if (line.startsWith('event: ')) event = line.slice(7);
    else if (line.startsWith('data: ')) data.push(line.slice(6));
  }
  if (!data.length) return;
  const payload = JSON.parse(data.join('\n'));

  if (event === 'state') onState(payload);
  else if (event === 'activity') onActivity(payload);
  else if (event === 'log') onLog(payload);
}

function setConnected(connected) {
  store.connected = connected;
  const element = $('#connection');
  element.classList.toggle('live', connected);
  element.classList.toggle('down', !connected);
  $('#connection-text').textContent = connected ? 'Live' : 'Reconnecting';
  renderBanner();
  renderStatus();
}

function onState(state) {
  store.state = state;
  store.stateIsCached = false;
  localStorage.setItem('spreadbot.state', JSON.stringify(state));
  renderStatus();
  renderBanner();
  const views = { overview: renderOverview, bots: renderBots, settings: renderSettings };
  views[store.tab]?.();
  document.title = state.halted ? 'Halted · SpreadBot' : `${state.bots.length} bot${state.bots.length === 1 ? '' : 's'} · SpreadBot`;
}

function onActivity(activity) {
  activity.fresh = true;
  store.activity.unshift(activity);
  store.activity.length = Math.min(store.activity.length, 300);
  if (store.tab === 'activity') renderActivity();
  if (store.tab === 'overview') renderOverviewActivity();

  if (activity.kind === 'fill') toast(activity.title, activity.detail, 'ok');
  if (activity.kind === 'finished') { toast(activity.title, activity.detail, activity.amount >= 0 ? 'ok' : 'bad'); loadOverviewHistory(); }
  if (activity.kind === 'halt' || activity.kind === 'streams') toast(activity.title, activity.detail, 'bad');
  if (activity.kind === 'fill' || activity.kind === 'finished') loadOrders();
}

function onLog(log) {
  store.logs.push(log);
  if (store.logs.length > 2000) store.logs.splice(0, store.logs.length - 2000);
  if (store.tab === 'logs') appendLog(log);
}

// ---------- Routing and shortcuts ----------

const TABS = ['overview', 'bots', 'markets', 'activity', 'history', 'logs', 'settings'];

function route() {
  const tab = TABS.includes(location.hash.slice(1)) ? location.hash.slice(1) : 'overview';
  store.tab = tab;
  for (const name of TABS) $(`#view-${name}`).hidden = name !== tab;
  for (const link of $$('#tabs a')) link.classList.toggle('active', link.dataset.tab === tab);
  render(tab);
}

function render(tab) {
  ({
    overview: () => { renderOverview(); loadOverviewHistory(); },
    bots: renderBots,
    markets: () => store.markets ? renderMarkets() : loadMarkets(),
    activity: () => { renderActivity(); loadOrders(); },
    history: loadHistory,
    logs: renderLogs,
    settings: () => { renderSettings(); renderPush(); },
  })[tab]?.();
}

document.addEventListener('keydown', event => {
  if (event.target.matches('input, select, textarea') || event.metaKey || event.ctrlKey || event.altKey || $('dialog[open]')) return;
  const number = Number(event.key);
  if (number >= 1 && number <= TABS.length) location.hash = TABS[number - 1];
  else if (event.key === '/') {
    const search = $(`#view-${store.tab} input[type="search"]`);
    if (search) { event.preventDefault(); search.focus(); }
  } else if (event.key === '?') $('#help').showModal();
  else if (event.key === 'r') render(store.tab);
});

// ---------- Top bar and banner ----------

function renderStatus() {
  const state = store.state;
  const pill = $('#status-pill');
  let text = 'Offline', status = 'offline';
  if (state) {
    if (state.halted) { text = 'Halted'; status = 'halted'; }
    else if (!store.connected && !store.stateIsCached) { text = 'Unreachable'; status = 'offline'; }
    else if (state.allocationPaused) { text = 'Paused'; status = 'paused'; }
    else { text = 'Trading'; status = 'live'; }
  }
  pill.textContent = text;
  pill.className = `pill ${status}`;
  $('#exchange').textContent = state ? `${state.exchange}${state.sandbox ? ' · testnet' : ''} · fee ${fmt.pct(state.feeRate * 100, 3)}` : '';
}

function renderBanner() {
  const banner = $('#banner');
  const state = store.state;
  let text = null, danger = false;
  if (state?.halted) { text = `Trading halted: ${state.haltReason || 'no reason given'}. Restart the bot to trade again.`; danger = true; }
  else if (!store.connected && state) text = `Can't reach the bot. Showing what it reported ${fmt.ago(state.now)}.`;
  else if (state?.allocationPaused) text = 'New bots are paused. Running bots carry on.';
  banner.hidden = !text;
  banner.textContent = text || '';
  banner.classList.toggle('danger', danger);
}

// ---------- Overview ----------

function renderOverview() {
  const state = store.state;
  if (!state) return;
  const totals = store.overviewHistory?.profitByBaseMarket || [];
  const today = new Date().toISOString().slice(0, 10);

  const cards = state.baseMarkets.flatMap(base => {
    const realized = totals.find(t => t.baseMarket === base.currency);
    const todayProfit = (store.overviewHistory?.dailyProfit || []).filter(d => d.baseMarket === base.currency && d.day.slice(0, 10) === today).reduce((sum, d) => sum + d.profit, 0);
    const inBots = base.totalValue - base.unallocated;
    const unrealized = state.bots.filter(b => b.baseMarket === base.currency).reduce((sum, b) => sum + b.unrealizedProfit, 0);
    const freeShare = base.totalValue > 0 ? Math.max(0, Math.min(100, base.unallocated / base.totalValue * 100)) : 0;
    return [
      card(`Equity ${base.currency}`, fmt.amount(base.totalValue), `${fmt.amount(base.total)} on the exchange`, h('div', { class: 'meter' },
        h('span', { style: { width: `${100 - freeShare}%`, background: 'var(--accent)' } }), h('span', { style: { width: `${freeShare}%`, background: 'var(--up)' } }))),
      card('Free / in bots', `${fmt.amount(base.unallocated, 6)}`, `${fmt.amount(inBots, 6)} ${base.currency} in ${base.bots} bot${base.bots === 1 ? '' : 's'}`),
      card('Open P&L', fmt.signed(unrealized), 'if every bot sold at the bid now', null, tone(unrealized)),
      card('Realized', fmt.signed(realized?.profit ?? 0), realized ? `${realized.bots} bots · ${Math.round(realized.winners / Math.max(1, realized.bots) * 100)}% won` : 'no finished bot yet', null, tone(realized?.profit)),
      card('Today', fmt.signed(todayProfit), 'realized since midnight UTC', null, tone(todayProfit)),
    ];
  });
  cards.push(card('Bots', `${state.bots.length} / ${state.maxBots}`, state.allocationPaused ? 'new bots paused' : `${state.configurations.length} configuration${state.configurations.length === 1 ? '' : 's'}`));
  replace($('#overview-money'), cards);

  renderHealth();
  renderOverviewBots();
  renderOverviewActivity();
  renderOverviewEquity();
}

function card(label, value, sub, extra, valueTone) {
  return h('div', { class: 'card' },
    h('div', { class: 'label' }, h('span', { text: label })),
    h('div', { class: `value ${valueTone || ''}`, text: value, title: value }),
    sub && h('div', { class: 'sub', text: sub }),
    extra);
}

function renderHealth() {
  const state = store.state;
  const age = date => date ? (Date.now() - new Date(date)) / 1000 : Infinity;
  //No dot for what hasn't happened yet (e.g. no resync since the start): it isn't a problem
  const dot = (seconds, stale, dead) => seconds === Infinity ? null : h('span', { class: `dot ${seconds > dead ? 'dead' : seconds > stale ? 'stale' : ''}` });
  const item = (label, value, indicator) => h('div', {}, h('div', { class: 'k' }, indicator, label), h('div', { class: 'v', text: value }));
  const streams = state.streams;
  replace($('#overview-health'), h('div', { class: 'health' },
    item('Prices', fmt.ago(streams.lastMarketUpdate), dot(age(streams.lastMarketUpdate), 30, 120)),
    item('Balances', fmt.ago(streams.lastBalanceUpdate), dot(age(streams.lastBalanceUpdate), 3600, Infinity)),
    item('Orders', fmt.ago(streams.lastOrderUpdate), null),
    item('Last resync', fmt.ago(streams.lastResync), dot(age(streams.lastResync), 600, 3600)),
    item('Resyncs', String(streams.resyncs), null),
    item('Markets', String(streams.markets), null)));
  $('#overview-uptime').textContent = `up ${fmt.duration(new Date(state.now) - new Date(state.startedAt))}`;
}

function renderOverviewBots() {
  const bots = [...store.state.bots].sort((a, b) => Math.abs(b.unrealizedProfit) - Math.abs(a.unrealizedProfit)).slice(0, 6);
  replace($('#overview-bots'), bots.length ? h('div', { class: 'rows' }, bots.map(bot => h('div', { onclick: () => openBot(bot.id) },
    h('strong', { text: bot.market }),
    h('span', { class: `state ${bot.state}`, text: stateLabel(bot.state) }),
    h('span', { class: 'grow muted', text: bot.order ? `${bot.order.side.toLowerCase()} ${fmt.amount(bot.order.quantity)} at ${fmt.amount(bot.order.limit)}` : '' }),
    h('span', { class: `mono ${tone(bot.unrealizedProfit)}`, text: fmt.signed(bot.unrealizedProfit) })))) : empty('No bots running.'));
}

function renderOverviewActivity() {
  replace($('#overview-activity'), store.activity.length ? store.activity.slice(0, 7).map(eventRow) : empty('Nothing yet. Fills, finished bots and alerts show up here.'));
}

function renderOverviewEquity() {
  const equity = store.overviewHistory?.equity || [];
  lineChart($('#overview-equity'), seriesByBase(equity, 'totalValue'), { format: fmt.amount });
}

async function loadOverviewHistory() {
  try {
    store.overviewHistory = await api('GET', '/api/history?days=7');
    if (store.tab === 'overview') renderOverview();
  } catch (error) { if (!(error instanceof Unauthorized)) console.warn(error); }
}

// ---------- Bots ----------

const stateLabel = state => ({ Buying: 'buying', BuyOrderActive: 'bid resting', Bought: 'holding', SellOrderActive: 'ask resting', FinishedWork: 'finished' })[state] || state;

function renderBots() {
  const state = store.state;
  if (!state) return;
  const { botsSearch, botsState, botsSort } = store.filters;
  let bots = state.bots.filter(bot => bot.market.toLowerCase().includes(botsSearch.toLowerCase()));
  if (botsState === 'buying') bots = bots.filter(bot => bot.state === 'Buying' || bot.state === 'BuyOrderActive');
  if (botsState === 'selling') bots = bots.filter(bot => bot.state === 'Bought' || bot.state === 'SellOrderActive');
  const sorters = {
    market: (a, b) => a.market.localeCompare(b.market),
    pnl: (a, b) => b.unrealizedProfit - a.unrealizedProfit,
    held: (a, b) => new Date(a.buyFilledAt || Date.now()) - new Date(b.buyFilledAt || Date.now()),
    spread: (a, b) => b.spreadPercentage - a.spreadPercentage,
  };
  bots.sort(sorters[botsSort]);

  replace($('#bots-list'), bots.length ? bots.map(botCard) : empty(state.bots.length ? 'No bot matches.' : 'No bots running. The coordinator allocates one when a market passes a configuration\'s filters.'));

  const dust = state.dust || [];
  $('#dust-panel').hidden = dust.length === 0;
  replace($('#dust-list'), h('div', { class: 'rows' }, dust.map(d => h('div', {}, h('span', { class: 'grow', text: d.market }), h('span', { class: 'mono', text: fmt.amount(d.amount) })))));
}

function botCard(bot) {
  const order = bot.order;
  const filled = order && order.quantity > 0 ? Math.min(100, order.filled / order.quantity * 100) : 0;
  const lossFrom = bot.sellAtLossFrom ? new Date(bot.sellAtLossFrom) : null;
  const heldSince = bot.buyFilledAt ? new Date(bot.buyFilledAt) : null;
  const timerShare = lossFrom && heldSince ? Math.min(100, (Date.now() - heldSince) / Math.max(1, lossFrom - heldSince) * 100) : 0;
  const holding = bot.held > 0 && (bot.state === 'Bought' || bot.state === 'SellOrderActive');

  return h('article', { class: 'bot', onclick: () => openBot(bot.id) },
    h('div', { class: 'bot-head' },
      h('span', { class: 'market', text: bot.market }),
      h('span', { class: `state ${bot.state}`, text: stateLabel(bot.state) }),
      h('span', { class: `pnl ${tone(bot.unrealizedProfit)}`, text: fmt.signed(bot.unrealizedProfit), title: 'Open P&L if it sold at the bid now' })),
    ladder(bot),
    h('div', { class: 'kv' },
      kv('Balance', fmt.amount(bot.balance, 6)),
      kv(`Held ${bot.coin}`, fmt.amount(bot.held, 4)),
      kv('Spread', fmt.pct(bot.spreadPercentage)),
      kv('Bought at', bot.boughtPrice ? fmt.amount(bot.boughtPrice) : '—'),
      kv('Break-even', bot.boughtPrice ? fmt.amount(bot.breakEven) : '—'),
      kv('Strategy', bot.strategy)),
    order && h('div', {},
      h('div', { class: 'bot-foot' }, h('span', { text: `${order.side === 'BUY' ? 'Bid' : 'Ask'} ${fmt.amount(order.quantity, 4)} @ ${fmt.amount(order.limit)}` }), h('span', { class: 'spacer' }), h('span', { text: order.status === 'UNKNOWN' ? 'placement unknown' : `${filled.toFixed(0)}% filled` })),
      h('div', { class: 'progress' }, h('span', { style: { width: `${filled}%` } }))),
    holding && lossFrom && h('div', {},
      h('div', { class: 'bot-foot' }, h('span', { text: Date.now() < lossFrom ? `Floor holds for ${fmt.duration(lossFrom - Date.now())}` : 'May sell at a loss now' }), h('span', { class: 'spacer' }), h('span', { text: `held ${fmt.duration(Date.now() - heldSince)}` })),
      h('div', { class: 'progress timer' }, h('span', { style: { width: `${timerShare}%` } }))),
    bot.orderRequestInFlight && h('div', { class: 'bot-foot' }, h('span', { class: 'chip warn', text: 'request in flight' })));
}

const kv = (label, value) => h('div', {}, h('div', { class: 'k', text: label }), h('div', { class: 'v', text: value, title: value }));

// Bid, ask, the bot's order and its break-even on one price scale
function ladder(bot) {
  const marks = [
    { price: bot.bid, label: 'bid', color: 'var(--up)' },
    { price: bot.ask, label: 'ask', color: 'var(--down)' },
    bot.order && { price: bot.order.limit, label: bot.order.side === 'BUY' ? 'our bid' : 'our ask', color: 'var(--accent)', mine: true },
    bot.boughtPrice > 0 && (bot.state === 'Bought' || bot.state === 'SellOrderActive') && { price: bot.breakEven, label: 'break-even', color: 'var(--warn)', dashed: true },
  ].filter(mark => mark && mark.price > 0);
  if (marks.length < 2) return null;

  const prices = marks.map(m => m.price);
  let low = Math.min(...prices), high = Math.max(...prices);
  const pad = (high - low || high * 0.01) * 0.25;
  low -= pad; high += pad;
  const x = price => 8 + (price - low) / (high - low) * 284;

  return s('svg', { class: 'ladder', viewBox: '0 0 300 54' },
    bot.bid > 0 && bot.ask > 0 && s('rect', { x: x(bot.bid), y: 14, width: Math.max(1, x(bot.ask) - x(bot.bid)), height: 14, fill: 'var(--up-soft)', rx: 3 }),
    s('line', { x1: 8, x2: 292, y1: 21, y2: 21, stroke: 'var(--line)' }),
    marks.map((mark, i) => [
      s('line', { x1: x(mark.price), x2: x(mark.price), y1: mark.mine ? 8 : 12, y2: mark.mine ? 34 : 30, stroke: mark.color, 'stroke-width': mark.mine ? 3 : 2, 'stroke-dasharray': mark.dashed ? '3 2' : null }),
      s('text', { x: Math.min(292, Math.max(8, x(mark.price))), y: i % 2 ? 50 : 44, 'text-anchor': 'middle' }, mark.label),
    ]));
}

async function openBot(id) {
  const bot = store.state?.bots.find(b => b.id === id);
  if (!bot) return;
  $('#detail-title').textContent = `${bot.market} · ${bot.strategy}`;
  const body = $('#detail-body');
  const orders = h('div', { class: 'table-wrap' }, empty('Loading orders…'));
  replace(body,
    h('div', { class: 'kv' },
      kv('State', stateLabel(bot.state)), kv('Allocated', fmt.amount(bot.allocated)), kv('Balance', fmt.amount(bot.balance)),
      kv(`Held ${bot.coin}`, fmt.amount(bot.held)), kv('Value now', fmt.amount(bot.value)), kv('Open P&L', fmt.signed(bot.unrealizedProfit)),
      kv('Bid', fmt.amount(bot.bid)), kv('Ask', fmt.amount(bot.ask)), kv('Tick', fmt.amount(bot.tick)),
      kv('Bought at', fmt.amount(bot.boughtPrice)), kv('Break-even', fmt.amount(bot.breakEven)), kv('Bought', bot.buyFilledAt ? fmt.datetime(bot.buyFilledAt) : '—')),
    ladder(bot),
    h('div', { class: 'panel-head' }, h('h2', { text: 'Order history' }), h('span', { class: 'muted', text: 'every state stored' })),
    orders,
    h('div', { class: 'actions', style: { marginTop: '14px', justifyContent: 'space-between' } },
      h('span', { class: 'muted', text: `Bot ${bot.id}` }),
      h('button', { class: 'button danger', text: 'Finish this bot…', onclick: () => finishBot(bot) })));
  $('#bot-detail').showModal();

  try {
    const rows = await api('GET', `/api/orders?botId=${encodeURIComponent(id)}&take=100`);
    replace(orders, rows.length ? ordersTable(rows) : empty('No stored orders.'));
  } catch (error) { replace(orders, empty(error.message)); }
}

$('#detail-close').addEventListener('click', () => $('#bot-detail').close());
$('#bot-detail').addEventListener('click', event => { if (event.target === event.currentTarget) event.currentTarget.close(); });

async function finishBot(bot) {
  const confirmed = await confirmAction({
    title: `Finish ${bot.market}?`,
    text: `It cancels its open order, then sells what it holds at market and releases its ${fmt.amount(bot.allocated)} ${bot.baseMarket}. Selling at market may cost more than its limit orders would.`,
    expect: bot.market,
    label: `Type ${bot.market} to confirm`,
    action: () => api('POST', `/api/bots/${bot.id}/finish`, { confirm: bot.market }),
  });
  if (confirmed) { $('#bot-detail').close(); toast('Finishing', `${bot.market} is cancelling and selling`, 'ok'); }
}

// ---------- Markets ----------

async function loadMarkets() {
  try {
    store.markets = await api('GET', '/api/markets?take=400');
    renderMarkets();
  } catch (error) { if (!(error instanceof Unauthorized)) toast('Markets', error.message, 'bad'); }
}

function renderMarkets() {
  const { marketsSearch, marketsEligible, marketsSort, marketsAsc } = store.filters;
  const keys = { symbol: m => m.symbol, spread: m => m.spreadPercentage, volume: m => m.quoteVolume ?? 0, change: m => m.percentChange ?? 0, last: m => m.last ?? 0, score: m => m.score };
  const markets = (store.markets || [])
    .filter(m => m.symbol.toLowerCase().includes(marketsSearch.toLowerCase()))
    .filter(m => !marketsEligible || m.verdicts.some(v => !v.rejection))
    .sort((a, b) => {
      const ka = keys[marketsSort](a), kb = keys[marketsSort](b);
      const order = typeof ka === 'string' ? ka.localeCompare(kb) : kb - ka;
      return marketsAsc ? -order : order;
    });

  for (const th of $$('#markets-table th[data-sort]')) {
    th.classList.toggle('sorted', th.dataset.sort === marketsSort);
    th.classList.toggle('asc', th.dataset.sort === marketsSort && marketsAsc);
  }

  const eligible = (store.markets || []).filter(m => m.verdicts.some(v => !v.rejection)).length;
  $('#markets-summary').textContent = `${eligible} of ${(store.markets || []).length} eligible`;

  replace($('#markets-table tbody'), markets.slice(0, 400).map(m => h('tr', {},
    h('td', {}, h('strong', { text: m.symbol }), m.bots ? h('span', { class: 'chip info', text: `${m.bots} bot${m.bots > 1 ? 's' : ''}` }) : null, m.status && m.status !== 'Online' ? h('span', { class: 'chip bad', text: m.status.toLowerCase() }) : null),
    h('td', { class: 'num', text: fmt.pct(m.spreadPercentage) }),
    h('td', { class: 'num', text: fmt.compact(m.quoteVolume) }),
    h('td', { class: `num ${tone(m.percentChange)}`, text: m.percentChange === null ? '—' : fmt.pct(m.percentChange, 1) }),
    h('td', { class: 'num', text: fmt.amount(m.last) }),
    h('td', { class: 'num', text: fmt.compact(m.score) }),
    h('td', {}, m.verdicts.map(v => h('span', { class: `chip ${v.rejection ? 'bad' : 'ok'}`, text: v.rejection ? v.rejection : `eligible · ${v.strategy}`, title: `${v.strategy} · ${fmt.amount(v.allocatedAmountOfBaseCurrency)}` }))))));
}

$$('#markets-table th[data-sort]').forEach(th => th.addEventListener('click', () => {
  const key = th.dataset.sort;
  store.filters.marketsAsc = store.filters.marketsSort === key ? !store.filters.marketsAsc : key === 'symbol';
  store.filters.marketsSort = key;
  renderMarkets();
}));

// ---------- Activity and orders ----------

const ICONS = { fill: '⇄', finished: '✓', placed: '•', partial: '◐', cancelled: '×', unknown: '?', halt: '■', streams: '!' };

function eventRow(activity) {
  const row = h('div', { class: `event ${activity.kind} ${activity.fresh ? 'fresh' : ''}`, onclick: activity.botId ? () => openBot(activity.botId) : null },
    h('div', { class: 'icon', text: ICONS[activity.kind] || '·' }),
    h('div', {}, h('div', { class: 'title', text: activity.title }), h('div', { class: 'detail', text: [activity.market, activity.detail].filter(Boolean).join(' · ') })),
    h('time', { text: fmt.time(activity.at), title: new Date(activity.at).toLocaleString() }));
  activity.fresh = false;
  return row;
}

function renderActivity() {
  const kind = store.filters.activityKind;
  const items = store.activity.filter(a => !kind || (kind === 'orders' ? ['placed', 'partial', 'cancelled', 'unknown'].includes(a.kind) : a.kind === kind));
  replace($('#activity-list'), items.length ? items.map(eventRow) : empty('Nothing yet since the dashboard started listening.'));
}

async function loadOrders() {
  if (store.tab !== 'activity') return;
  try {
    const market = store.filters.ordersMarket.trim().toUpperCase();
    store.orders = await api('GET', `/api/orders?take=300${market ? `&market=${encodeURIComponent(market)}` : ''}`);
    replace($('#orders-table tbody'), ordersRows(store.orders));
  } catch (error) { if (!(error instanceof Unauthorized)) toast('Orders', error.message, 'bad'); }
}

function ordersRows(rows) {
  return rows.map(o => h('tr', {},
    h('td', { text: o.recordedAt ? fmt.datetime(o.recordedAt) : '—' }),
    h('td', { text: o.market }),
    h('td', { class: o.side === 'BUY' ? 'up' : 'down', text: o.side.toLowerCase() }),
    h('td', {}, h('span', { class: `chip ${o.status === 'CLOSED' ? (o.filled > 0 ? 'ok' : '') : o.status === 'UNKNOWN' ? 'warn' : 'info'}`, text: o.status === 'CLOSED' ? (o.filled > 0 ? 'filled' : 'cancelled') : o.status.toLowerCase() })),
    h('td', { class: 'num', text: `${fmt.amount(o.filled, 4)} / ${fmt.amount(o.quantity, 4)}` }),
    h('td', { class: 'num', text: fmt.amount(o.limit) }),
    h('td', { class: 'num', text: o.proceeds ? fmt.amount(o.proceeds) : '—' })));
}

function ordersTable(rows) {
  return h('table', { class: 'table compact' },
    h('thead', {}, h('tr', {}, ['Recorded', 'Market', 'Side', 'Status', 'Filled', 'Limit', 'Proceeds'].map((title, i) => h('th', { class: i >= 4 ? 'num' : '', text: title })))),
    h('tbody', {}, ordersRows(rows)));
}

// ---------- History ----------

async function loadHistory() {
  try {
    store.history = await api('GET', `/api/history?days=${store.filters.historyDays}`);
    renderHistory();
  } catch (error) { if (!(error instanceof Unauthorized)) toast('History', error.message, 'bad'); }
}

function renderHistory() {
  const history = store.history;
  if (!history) return;

  replace($('#history-totals'), history.profitByBaseMarket.length ? history.profitByBaseMarket.flatMap(total => [
    card(`Realized ${total.baseMarket}`, fmt.signed(total.profit), 'all time', null, tone(total.profit)),
    card('Bots finished', String(total.bots), `${total.winners} won · ${Math.round(total.winners / Math.max(1, total.bots) * 100)}%`),
    card('Average bot', fmt.signed(total.profit / Math.max(1, total.bots)), `held ${fmt.duration(total.averageMinutes * 60000)} on average`, null, tone(total.profit)),
  ]) : [card('Realized', '0', 'no bot has finished yet')]);

  lineChart($('#history-equity'), seriesByBase(history.equity, 'totalValue'), { format: fmt.amount });
  barChart($('#history-daily'), history.dailyProfit, { format: fmt.signed });
  hbars($('#history-configurations'), history.profitByConfiguration, group => `${group.bots} bots · ${Math.round(group.winners / Math.max(1, group.bots) * 100)}% won${group.active ? '' : ' · no longer configured'}`);
  const markets = history.profitByMarket;
  hbars($('#history-markets'), markets.length > 16 ? [...markets.slice(0, 8), ...markets.slice(-8)] : markets, group => `${group.bots} bots · ${Math.round(group.winners / Math.max(1, group.bots) * 100)}% won`);

  replace($('#history-bots tbody'), history.finishedBots.map(bot => h('tr', {},
    h('td', { text: bot.finishedAt ? fmt.datetime(bot.finishedAt) : '—' }),
    h('td', { text: bot.market }),
    h('td', { text: bot.strategy }),
    h('td', { class: 'num', text: bot.finishedAt ? fmt.duration(new Date(bot.finishedAt) - new Date(bot.allocatedAt)) : '—' }),
    h('td', { class: `num ${tone(bot.profit)}`, text: fmt.signed(bot.profit) }),
    h('td', { class: `num ${tone(bot.profit)}`, text: bot.allocated ? fmt.pct(bot.profit / bot.allocated * 100) : '—' }))));
}

function hbars(container, groups, meta) {
  if (!groups.length) { replace(container, empty('No finished bots in this range.')); return; }
  const max = Math.max(...groups.map(g => Math.abs(g.profit)), 1e-12);
  replace(container, h('div', { class: 'bars' }, groups.map(group => [
    h('div', {},
      h('span', { class: 'name', text: group.name, title: group.name }),
      h('div', { class: 'track' }, h('span', { style: { left: '0', width: `${Math.abs(group.profit) / max * 100}%`, background: group.profit >= 0 ? 'var(--up)' : 'var(--down)' } })),
      h('span', { class: `amount ${tone(group.profit)}`, text: fmt.signed(group.profit) }),
      h('span', { class: 'meta', text: meta(group) })),
  ])));
}

// ---------- Charts ----------

const PALETTE = ['#5b8cff', '#38bd94', '#f2b84b', '#c084fc', '#ff7a6b', '#22d3ee'];

function seriesByBase(points, field) {
  const bases = [...new Set(points.map(p => p.baseMarket))];
  return bases.map((base, i) => ({ name: base, color: PALETTE[i % PALETTE.length], points: points.filter(p => p.baseMarket === base).map(p => ({ x: new Date(p.at), y: p[field] })) }));
}

function lineChart(container, series, { format }) {
  const all = series.flatMap(serie => serie.points);
  if (all.length < 2) { replace(container, empty('Not enough samples yet: one is taken every 5 minutes.')); return; }

  const width = container.clientWidth || 600, height = container.clientHeight || 180;
  const left = 8, right = 8, top = 10, bottom = 22;
  const xs = all.map(p => +p.x), ys = all.map(p => p.y);
  const x0 = Math.min(...xs), x1 = Math.max(...xs);
  let y0 = Math.min(...ys), y1 = Math.max(...ys);
  if (y0 === y1) { y0 -= Math.abs(y0) * 0.01 || 1; y1 += Math.abs(y1) * 0.01 || 1; }
  const pad = (y1 - y0) * 0.12; y0 -= pad; y1 += pad;
  const px = x => left + (x - x0) / Math.max(1, x1 - x0) * (width - left - right);
  const py = y => top + (1 - (y - y0) / (y1 - y0)) * (height - top - bottom);

  const svg = s('svg', { viewBox: `0 0 ${width} ${height}` },
    [0.25, 0.5, 0.75].map(f => s('line', { class: 'grid-line', x1: left, x2: width - right, y1: top + f * (height - top - bottom), y2: top + f * (height - top - bottom) })),
    s('g', { class: 'axis' },
      s('text', { x: left, y: height - 6 }, fmt.day(x0)),
      s('text', { x: width - right, y: height - 6, 'text-anchor': 'end' }, fmt.day(x1)),
      s('text', { x: width - right, y: top + 10, 'text-anchor': 'end' }, format(y1 - pad)),
      s('text', { x: width - right, y: height - bottom - 4, 'text-anchor': 'end' }, format(y0 + pad))),
    series.map(serie => {
      const sorted = [...serie.points].sort((a, b) => a.x - b.x);
      const line = sorted.map((p, i) => `${i ? 'L' : 'M'}${px(+p.x).toFixed(1)},${py(p.y).toFixed(1)}`).join('');
      const area = `${line}L${px(+sorted.at(-1).x).toFixed(1)},${height - bottom}L${px(+sorted[0].x).toFixed(1)},${height - bottom}Z`;
      return [s('path', { d: area, fill: serie.color, opacity: 0.12 }), s('path', { d: line, fill: 'none', stroke: serie.color, 'stroke-width': 2, 'stroke-linejoin': 'round' })];
    }));

  const cursor = s('line', { x1: 0, x2: 0, y1: top, y2: height - bottom, stroke: 'var(--muted)', 'stroke-dasharray': '2 3', opacity: 0 });
  svg.append(cursor);
  const tip = h('div', { class: 'tooltip', hidden: true });
  svg.addEventListener('pointermove', event => {
    const rect = svg.getBoundingClientRect();
    const at = x0 + ((event.clientX - rect.left) / rect.width * width - left) / (width - left - right) * (x1 - x0);
    const nearest = series.map(serie => ({ serie, point: serie.points.reduce((best, p) => Math.abs(p.x - at) < Math.abs(best.x - at) ? p : best, serie.points[0]) })).filter(n => n.point);
    if (!nearest.length) return;
    const x = px(+nearest[0].point.x);
    cursor.setAttribute('x1', x); cursor.setAttribute('x2', x); cursor.setAttribute('opacity', 1);
    tip.hidden = false;
    tip.style.left = `${x / width * 100}%`;
    tip.style.top = `${top + 10}px`;
    replace(tip, h('div', { class: 'muted', text: new Date(nearest[0].point.x).toLocaleString() }), nearest.map(n => h('div', {}, h('span', { style: { color: n.serie.color }, text: `${n.serie.name} ` }), format(n.point.y))));
  });
  svg.addEventListener('pointerleave', () => { tip.hidden = true; cursor.setAttribute('opacity', 0); });

  replace(container, svg, tip, series.length > 1 ? h('div', { class: 'legend' }, series.map(serie => h('span', {}, h('i', { style: { background: serie.color } }), serie.name))) : null);
}

function barChart(container, days, { format }) {
  if (!days.length) { replace(container, empty('No bot finished in this range.')); return; }
  const byDay = new Map();
  for (const d of days) byDay.set(d.day.slice(0, 10), (byDay.get(d.day.slice(0, 10)) || 0) + d.profit);
  const entries = [...byDay.entries()].sort(([a], [b]) => a.localeCompare(b));

  const width = container.clientWidth || 600, height = container.clientHeight || 180;
  const top = 10, bottom = 22;
  const max = Math.max(...entries.map(([, v]) => Math.abs(v)), 1e-12);
  const zero = top + (height - top - bottom) / 2;
  const slot = width / entries.length;
  const barWidth = Math.max(2, Math.min(28, slot * 0.7));
  const tip = h('div', { class: 'tooltip', hidden: true });

  const svg = s('svg', { viewBox: `0 0 ${width} ${height}` },
    s('line', { class: 'grid-line', x1: 0, x2: width, y1: zero, y2: zero }),
    entries.map(([day, value], i) => {
      const barHeight = Math.abs(value) / max * (height - top - bottom) / 2;
      const x = i * slot + (slot - barWidth) / 2;
      const rect = s('rect', { x, y: value >= 0 ? zero - barHeight : zero, width: barWidth, height: Math.max(1, barHeight), rx: 3, fill: value >= 0 ? 'var(--up)' : 'var(--down)' });
      rect.addEventListener('pointerenter', () => {
        tip.hidden = false;
        tip.style.left = `${(x + barWidth / 2) / width * 100}%`;
        tip.style.top = `${top + 10}px`;
        replace(tip, h('div', { class: 'muted', text: fmt.day(day) }), h('div', { class: tone(value), text: format(value) }));
      });
      rect.addEventListener('pointerleave', () => { tip.hidden = true; });
      return rect;
    }),
    s('g', { class: 'axis' },
      s('text', { x: 0, y: height - 6 }, fmt.day(entries[0][0])),
      s('text', { x: width, y: height - 6, 'text-anchor': 'end' }, fmt.day(entries.at(-1)[0]))));
  replace(container, svg, tip);
}

// ---------- Logs ----------

function logLine(log) {
  const search = store.filters.logsSearch;
  const content = h('span', {});
  if (search) {
    const lower = log.content.toLowerCase(), needle = search.toLowerCase();
    let from = 0, index;
    while ((index = lower.indexOf(needle, from)) >= 0) {
      content.append(log.content.slice(from, index), h('mark', { text: log.content.slice(index, index + needle.length) }));
      from = index + needle.length;
    }
    content.append(log.content.slice(from));
  } else content.textContent = log.content;
  return h('div', { class: log.level }, h('time', { text: fmt.time(log.at), title: new Date(log.at).toLocaleString() }), content);
}

const logVisible = log => (!store.filters.logsLevel || log.level === store.filters.logsLevel || (store.filters.logsLevel === 'Error' && log.level === 'UnexpectedError'))
  && (!store.filters.logsSearch || log.content.toLowerCase().includes(store.filters.logsSearch.toLowerCase()));

function renderLogs() {
  const list = $('#logs-list');
  const logs = store.logs.filter(logVisible).slice(-800);
  replace(list, logs.length ? logs.map(logLine) : empty('No logs yet.'));
  if ($('#logs-follow').checked) list.scrollTop = list.scrollHeight;
}

function appendLog(log) {
  if (!logVisible(log)) return;
  const list = $('#logs-list');
  if (list.firstElementChild?.classList.contains('empty')) list.replaceChildren();
  list.append(logLine(log));
  while (list.childElementCount > 1000) list.firstElementChild.remove();
  if ($('#logs-follow').checked) list.scrollTop = list.scrollHeight;
}

// ---------- Settings and controls ----------

function renderSettings() {
  const state = store.state;
  if (!state) return;
  const toggle = $('#toggle-allocation');
  toggle.textContent = state.allocationPaused ? 'Resume' : 'Pause';
  toggle.disabled = state.halted;
  $('#halt').disabled = state.halted;
  $('#resync').disabled = state.halted;

  replace($('#settings-configurations'), state.configurations.length ? h('div', { class: 'rows' }, state.configurations.map(c => h('div', {},
    h('span', { class: 'chip info', text: c.strategy }),
    h('span', { class: 'grow', text: `${fmt.amount(c.allocatedAmountOfBaseCurrency)} ${c.baseMarket} · spread ≥ ${c.minimumSpreadPercentage}% · volume ≥ ${fmt.compact(c.minimumQuoteVolume)} · loss after ${c.minutesForLoss} min` }),
    h('span', { class: 'muted', text: `${c.bots} bot${c.bots === 1 ? '' : 's'}` })))) : empty('No configurations.'));
}

$('#toggle-allocation').addEventListener('click', async () => {
  const pause = !store.state?.allocationPaused;
  if (pause && !await confirmAction({ title: 'Pause new bots?', text: 'Running bots carry on; no market gets a new bot until you resume.', action: () => api('POST', '/api/controls/allocation', { paused: true }) })) return;
  if (!pause) await guarded(() => api('POST', '/api/controls/allocation', { paused: false }));
  toast(pause ? 'Paused' : 'Resumed', pause ? 'No new bots' : 'New bots can be allocated again', 'ok');
});

$('#resync').addEventListener('click', async () => {
  if (await guarded(() => api('POST', '/api/controls/resync'))) toast('Resync requested', 'Balances, markets, tickers and orders are being refetched', 'ok');
});

$('#halt').addEventListener('click', async () => {
  const confirmed = await confirmAction({
    title: 'Halt all trading?',
    text: 'Every bot cancels its open order, the session is saved, and the bot exits. It does not sell what bots hold. Restart it to trade again.',
    expect: 'HALT',
    label: 'Type HALT to confirm',
    action: () => api('POST', '/api/controls/halt', { confirm: 'HALT' }),
  });
  if (confirmed) toast('Halting', 'Orders are being cancelled; the bot will exit', 'bad');
});

$('#sign-out').addEventListener('click', () => {
  localStorage.removeItem('spreadbot.token');
  localStorage.removeItem('spreadbot.state');
  location.reload();
});

async function guarded(action) {
  try {
    await action();
    return true;
  } catch (error) {
    if (!(error instanceof Unauthorized)) toast('Failed', error.message, 'bad');
    return false;
  }
}

function confirmAction({ title, text, expect, label, action }) {
  const dialog = $('#confirm');
  $('#confirm-title').textContent = title;
  $('#confirm-text').textContent = text;
  $('#confirm-label').textContent = label || '';
  const input = $('#confirm-input');
  input.hidden = !expect;
  input.value = '';
  $('#confirm-error').textContent = '';
  dialog.showModal();
  if (expect) input.focus();

  return new Promise(resolve => {
    const form = $('#confirm-form');
    const submit = async event => {
      event.preventDefault();
      if (expect && input.value.trim().toUpperCase() !== expect.toUpperCase()) { $('#confirm-error').textContent = `Type ${expect} exactly.`; return; }
      try {
        $('#confirm-ok').disabled = true;
        await action();
        finish(true);
      } catch (error) {
        if (error instanceof Unauthorized) finish(false);
        else $('#confirm-error').textContent = error.message;
      } finally {
        $('#confirm-ok').disabled = false;
      }
    };
    const finish = result => {
      form.removeEventListener('submit', submit);
      dialog.removeEventListener('cancel', cancel);
      $('#confirm-cancel').removeEventListener('click', cancel);
      dialog.close();
      resolve(result);
    };
    const cancel = () => finish(false);
    form.addEventListener('submit', submit);
    dialog.addEventListener('cancel', cancel);
    $('#confirm-cancel').addEventListener('click', cancel);
  });
}

// ---------- Push notifications ----------

const CATEGORY_TEXT = {
  halt: ['Halt', 'trading stopped for good'],
  finished: ['Finished bots', 'each bot\'s profit or loss'],
  fills: ['Fills', 'every buy and sell that filled'],
  errors: ['Errors', 'unexpected errors, at most every 10 min'],
  streams: ['Market data', 'prices stopped or came back'],
};

async function pushSubscription() {
  if (!('serviceWorker' in navigator) || !('PushManager' in window)) return null;
  const registration = await navigator.serviceWorker.ready;
  return registration.pushManager.getSubscription();
}

async function renderPush() {
  const status = $('#push-status'), device = $('#push-device'), toggle = $('#push-toggle'), test = $('#push-test');
  try {
    store.push = store.push || await api('GET', '/api/push');
  } catch (error) { if (!(error instanceof Unauthorized)) status.textContent = error.message; return; }

  const supported = window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
  const subscription = supported ? await pushSubscription() : null;
  let chosen = store.push.categories.filter(c => c !== 'fills');
  if (subscription) {
    try { chosen = (await api('POST', '/api/push/subscriptions/lookup', { endpoint: subscription.endpoint })).categories || []; } catch { }
  }

  replace($('#push-categories'), store.push.categories.map(category => h('label', {},
    h('input', { type: 'checkbox', value: category, checked: chosen.includes(category), disabled: !subscription, onchange: () => savePushCategories() }),
    h('span', {}, CATEGORY_TEXT[category]?.[0] || category, h('small', { text: CATEGORY_TEXT[category]?.[1] || '' })))));

  status.textContent = !store.push.available ? 'off: set Dashboard.PublicUrl' : subscription ? 'on for this device' : 'off for this device';
  device.textContent = !supported
    ? 'This browser can\'t receive push here: open the dashboard over HTTPS (tailscale serve) and, on iPhone, install it to the home screen first.'
    : !store.push.available ? 'The bot has no Dashboard.PublicUrl, which push services need.'
      : Notification.permission === 'denied' ? 'Notifications are blocked for this site in the browser\'s settings.'
        : subscription ? 'This device gets the categories ticked above.' : 'Get alerts on this device even when the dashboard is closed.';
  toggle.textContent = subscription ? 'Disable' : 'Enable';
  toggle.disabled = !supported || !store.push.available || Notification.permission === 'denied';
  test.disabled = !subscription;
}

const chosenCategories = () => $$('#push-categories input:checked').map(input => input.value);

async function savePushCategories() {
  const subscription = await pushSubscription();
  if (!subscription) return;
  await guarded(() => api('POST', '/api/push/subscriptions', { ...subscription.toJSON(), categories: chosenCategories() }));
}

$('#push-toggle').addEventListener('click', async () => {
  const existing = await pushSubscription();
  if (existing) {
    await guarded(() => api('POST', '/api/push/subscriptions/remove', { endpoint: existing.endpoint }));
    await existing.unsubscribe();
  } else {
    if (await Notification.requestPermission() !== 'granted') { renderPush(); return; }
    const registration = await navigator.serviceWorker.ready;
    try {
      const subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64UrlToBytes(store.push.publicKey) });
      const categories = store.push.categories.filter(c => c !== 'fills');
      await api('POST', '/api/push/subscriptions', { ...subscription.toJSON(), categories });
      toast('Notifications on', 'This device gets alerts now', 'ok');
    } catch (error) { if (!(error instanceof Unauthorized)) toast('Couldn\'t enable notifications', error.message, 'bad'); }
  }
  renderPush();
});

$('#push-test').addEventListener('click', async () => {
  if (await guarded(() => api('POST', '/api/push/test'))) toast('Test sent', 'It should arrive in a few seconds', 'ok');
});

function base64UrlToBytes(text) {
  const base64 = (text + '='.repeat((4 - text.length % 4) % 4)).replace(/-/g, '+').replace(/_/g, '/');
  return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
}

// ---------- Small pieces ----------

const empty = text => h('div', { class: 'empty', text });

function toast(title, text, kind = '') {
  if (document.hidden) return;
  const element = h('div', { class: `toast ${kind}` }, h('strong', { text: title }), text && h('span', { text }));
  $('#toasts').append(element);
  setTimeout(() => element.remove(), 5000);
}

function downloadCsv(name, header, rows) {
  const cell = value => {
    const text = value === null || value === undefined ? '' : String(value);
    return /[",\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  };
  const csv = [header, ...rows].map(row => row.map(cell).join(',')).join('\n');
  const link = h('a', { href: URL.createObjectURL(new Blob([csv], { type: 'text/csv' })), download: name });
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(link.href), 1000);
}

$('#orders-export').addEventListener('click', () => downloadCsv(`spreadbot-orders-${new Date().toISOString().slice(0, 10)}.csv`,
  ['recordedAt', 'market', 'side', 'type', 'status', 'quantity', 'limit', 'filled', 'proceeds', 'commission', 'coinCommission', 'orderId', 'clientOrderId', 'botId'],
  store.orders.map(o => [o.recordedAt, o.market, o.side, o.type, o.status, o.quantity, o.limit, o.filled, o.proceeds, o.commission, o.coinCommission, o.id, o.clientOrderId, o.botId])));

$('#history-export').addEventListener('click', () => downloadCsv(`spreadbot-bots-${new Date().toISOString().slice(0, 10)}.csv`,
  ['finishedAt', 'allocatedAt', 'market', 'baseMarket', 'strategy', 'allocated', 'profit', 'botId'],
  (store.history?.finishedBots || []).map(b => [b.finishedAt, b.allocatedAt, b.market, b.baseMarket, b.strategy, b.allocated, b.profit, b.id])));

// ---------- Inputs ----------

function bindSegmented(selector, key, onChange) {
  const group = $(selector);
  group.addEventListener('click', event => {
    const button = event.target.closest('button');
    if (!button) return;
    $$('button', group).forEach(b => b.classList.toggle('active', b === button));
    store.filters[key] = button.dataset.value;
    onChange();
  });
}

bindSegmented('#bots-state', 'botsState', renderBots);
bindSegmented('#activity-kind', 'activityKind', renderActivity);
bindSegmented('#logs-level', 'logsLevel', renderLogs);
bindSegmented('#history-range', 'historyDays', loadHistory);
$('#bots-search').addEventListener('input', event => { store.filters.botsSearch = event.target.value; renderBots(); });
$('#bots-sort').addEventListener('change', event => { store.filters.botsSort = event.target.value; renderBots(); });
$('#markets-search').addEventListener('input', event => { store.filters.marketsSearch = event.target.value; renderMarkets(); });
$('#markets-eligible').addEventListener('change', event => { store.filters.marketsEligible = event.target.checked; renderMarkets(); });
$('#markets-refresh').addEventListener('click', loadMarkets);
$('#logs-search').addEventListener('input', event => { store.filters.logsSearch = event.target.value; renderLogs(); });
$('#logs-follow').addEventListener('change', () => { if ($('#logs-follow').checked) $('#logs-list').scrollTop = $('#logs-list').scrollHeight; });
let ordersTimer;
$('#orders-market').addEventListener('input', event => {
  store.filters.ordersMarket = event.target.value;
  clearTimeout(ordersTimer);
  ordersTimer = setTimeout(loadOrders, 300);
});

const theme = localStorage.getItem('spreadbot.theme') || 'auto';
document.documentElement.dataset.theme = theme;
$$('#theme button').forEach(button => button.classList.toggle('active', button.dataset.value === theme));
$('#theme').addEventListener('click', event => {
  const button = event.target.closest('button');
  if (!button) return;
  localStorage.setItem('spreadbot.theme', button.dataset.value);
  document.documentElement.dataset.theme = button.dataset.value;
  $$('#theme button').forEach(b => b.classList.toggle('active', b === button));
});

// ---------- Sign in ----------

function showLogin(error) {
  streamAbort?.abort();
  $('#login-error').textContent = error || '';
  if (!$('#login').open) $('#login').showModal();
}

$('#login-form').addEventListener('submit', async event => {
  event.preventDefault();
  const token = $('#login-token').value.trim();
  if (!token) return;
  const response = await fetch('/api/state', { headers: { Authorization: `Bearer ${token}` } }).catch(() => null);
  if (!response) { $('#login-error').textContent = 'Can\'t reach the bot.'; return; }
  if (response.status === 401) { $('#login-error').textContent = 'That token isn\'t the bot\'s.'; return; }
  store.token = token;
  localStorage.setItem('spreadbot.token', token);
  $('#login').close();
  start();
});
$('#login').addEventListener('cancel', event => event.preventDefault());

// ---------- Install, service worker, refreshes ----------

let installPrompt;
window.addEventListener('beforeinstallprompt', event => { event.preventDefault(); installPrompt = event; $('#install').hidden = false; });
$('#install').addEventListener('click', async () => { $('#install').hidden = true; await installPrompt?.prompt(); installPrompt = null; });

if ('serviceWorker' in navigator && window.isSecureContext) {
  navigator.serviceWorker.register('/sw.js').catch(error => console.warn('Service worker', error));
  navigator.serviceWorker.addEventListener('message', event => { if (event.data?.navigate) location.hash = event.data.navigate; });
}

// The live state arrives every 2 s; the rest refreshes while its section is open
setInterval(() => {
  if (document.hidden || !store.token) return;
  if (store.tab === 'markets') loadMarkets();
  if (store.tab === 'overview') loadOverviewHistory();
  if (store.tab === 'bots' || store.tab === 'overview') ({ bots: renderBots, overview: renderOverview })[store.tab]();
}, 15000);

let started = false;
async function start() {
  if (!started) {
    started = true;
    window.addEventListener('hashchange', route);
  }
  try {
    const [activity, logs] = await Promise.all([api('GET', '/api/activity'), api('GET', '/api/logs')]);
    store.activity = activity;
    store.logs = logs.reverse();
  } catch (error) { if (error instanceof Unauthorized) return; }
  route();
  connect();
}

renderStatus();
if (store.state) route();
if (store.token) start(); else showLogin();

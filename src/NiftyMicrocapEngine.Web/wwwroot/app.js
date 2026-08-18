// Nifty Microcap Engine dashboard client. Vanilla JS, no build step -
// calls the /api/scan and /api/scan/{symbolId} endpoints and renders the
// Scan Results table and Symbol Drill-Down view per build spec section 20.2.

const state = {
  rows: [],
  sortKey: 'confidence',
  sortDesc: true,
  decisionFilter: '',
  symbolFilter: '',
  currentDrillDownSymbolId: null,
  chart: null // { api, candleSeries } — the active TradingView chart instance, so re-rendering (timeframe switch) disposes the old one first
};

const el = (id) => document.getElementById(id);

function todayIso() {
  const d = new Date();
  return d.toISOString().slice(0, 10);
}

el('date-input').value = todayIso();

el('run-scan-btn').addEventListener('click', runScan);
el('back-to-results-btn').addEventListener('click', showResultsView);
el('view-chart-btn').addEventListener('click', () => showChart(state.currentDrillDownSymbolId));
el('back-to-drilldown-btn').addEventListener('click', showDrillDownView);
el('chart-timeframe').addEventListener('change', () => showChart(state.currentDrillDownSymbolId));
el('decision-filter').addEventListener('change', (e) => { state.decisionFilter = e.target.value; renderTable(); });
el('symbol-filter').addEventListener('input', (e) => { state.symbolFilter = e.target.value.toUpperCase(); renderTable(); });

document.querySelectorAll('#scan-table thead th[data-sort]').forEach((th) => {
  th.addEventListener('click', () => {
    const key = th.getAttribute('data-sort');
    if (state.sortKey === key) {
      state.sortDesc = !state.sortDesc;
    } else {
      state.sortKey = key;
      state.sortDesc = true;
    }
    renderTable();
  });
});

async function runScan() {
  const btn = el('run-scan-btn');
  const status = el('scan-status');
  btn.disabled = true;
  status.className = 'status-text';
  status.textContent = 'Running scan...';

  try {
    const date = el('date-input').value;
    // Explicit user action — always bypasses the server-side scan cache
    // (see DashboardEndpoints.GetOrRunScanAsync's doc comment): clicking
    // "Run Scan" means "run one now," not "show me whatever's cached."
    // Drill-down/chart requests deliberately don't pass this, so they
    // benefit from reusing this scan's result instead of recomputing it.
    const res = await fetch('/api/scan?date=' + encodeURIComponent(date) + '&refresh=true');
    if (!res.ok) throw new Error('Scan request failed: ' + res.status);
    const data = await res.json();

    state.rows = data.rows || [];
    el('stat-stage1-scanned').textContent = data.stage1Scanned;
    el('stat-stage1-excluded').textContent = data.stage1Excluded;
    el('stat-stage2-size').textContent = data.stage2ShortlistSize;
    el('stat-durations').textContent = data.stage1DurationMs + 'ms / ' + data.stage2DurationMs + 'ms';

    status.textContent = 'Scan complete: ' + state.rows.length + ' Stage 2 result(s).';
    renderTable();
  } catch (err) {
    status.className = 'status-text error';
    status.textContent = String(err.message || err);
  } finally {
    btn.disabled = false;
  }
}

function renderTable() {
  const tbody = el('scan-table-body');
  tbody.innerHTML = '';

  let filtered = state.rows.filter((r) => {
    if (state.decisionFilter && r.decision !== state.decisionFilter) return false;
    if (state.symbolFilter && r.nseSymbol.toUpperCase().indexOf(state.symbolFilter) === -1) return false;
    return true;
  });

  filtered.sort((a, b) => {
    const av = a[state.sortKey];
    const bv = b[state.sortKey];
    let cmp;
    if (typeof av === 'string') {
      cmp = av.localeCompare(bv);
    } else {
      cmp = (av ?? -Infinity) - (bv ?? -Infinity);
    }
    return state.sortDesc ? -cmp : cmp;
  });

  if (filtered.length === 0) {
    tbody.innerHTML = '<tr class="empty-row"><td colspan="5">No results match the current filters.</td></tr>';
    return;
  }

  for (const row of filtered) {
    const tr = document.createElement('tr');
    tr.addEventListener('click', () => showDrillDown(row.symbolId));

    tr.innerHTML =
      '<td>' + escapeHtml(row.nseSymbol) + '</td>' +
      '<td><span class="decision-badge decision-' + row.decision + '">' + formatDecision(row.decision) + '</span></td>' +
      '<td class="numeric">' + row.confidence.toFixed(1) + '</td>' +
      '<td class="numeric">' + (row.riskReward != null ? row.riskReward.toFixed(2) : '-') + '</td>' +
      '<td class="numeric">' + (row.relativeStrength != null ? row.relativeStrength.toFixed(2) : '-') + '</td>';

    tbody.appendChild(tr);
  }
}

async function showDrillDown(symbolId) {
  state.currentDrillDownSymbolId = symbolId;
  const date = el('date-input').value;
  const res = await fetch('/api/scan/' + symbolId + '?date=' + encodeURIComponent(date));
  if (!res.ok) return;
  const dd = await res.json();

  el('dd-symbol').textContent = dd.nseSymbol;
  const decisionBadge = el('dd-decision');
  decisionBadge.textContent = formatDecision(dd.decision);
  decisionBadge.className = 'decision-badge decision-' + dd.decision;
  el('dd-confidence').textContent = 'Confidence: ' + dd.confidence.toFixed(1);

  el('dd-reasoning').textContent = dd.reasoningText;

  const layerBody = el('dd-layer-table-body');
  layerBody.innerHTML = '';
  for (const layer of dd.layerScores) {
    const pct = layer.maxPoints > 0 ? Math.min(100, Math.abs(layer.contribution) / layer.maxPoints * 100) : 0;
    const barClass = layer.contribution >= 0 ? 'positive' : 'negative';
    const tr = document.createElement('tr');
    tr.innerHTML =
      '<td>' + escapeHtml(layer.layerName) + '</td>' +
      '<td class="numeric">' + layer.contribution.toFixed(1) + '</td>' +
      '<td class="numeric">' + layer.maxPoints.toFixed(0) + '</td>' +
      '<td><div class="layer-bar-track"><div class="layer-bar-fill ' + barClass + '" style="width:' + pct + '%"></div></div></td>';
    layerBody.appendChild(tr);
  }

  const tradePlanEl = el('dd-trade-plan');
  if (dd.tradePlan) {
    const tp = dd.tradePlan;
    tradePlanEl.innerHTML =
      tpRow('Entry', tp.entry) + tpRow('Stop Loss', tp.stopLoss) +
      tpRow('Target 1', tp.target1) + tpRow('Target 2', tp.target2) +
      tpRow('Target 3', tp.target3) + tpRow('R:R', tp.riskRewardRatio.toFixed(2)) +
      tpRow('Risk %', (tp.riskPercent * 100).toFixed(2) + '%') +
      tpRow('Invalidation', tp.invalidationLevel) +
      tpRow('Est. Duration', tp.estimatedDuration || (tp.durationDataQualityFlag || 'N/A'));
  } else {
    tradePlanEl.textContent = 'No trade plan - decision is not Buy/StrongBuy.';
  }

  const flagsEl = el('dd-flags');
  flagsEl.innerHTML = '';
  const allFlags = (dd.hardGateFailures || []).concat(dd.dataQualityFailureReasons || []);
  if (allFlags.length === 0) {
    flagsEl.innerHTML = '<li class="muted">None</li>';
  } else {
    for (const flag of allFlags) {
      const li = document.createElement('li');
      li.textContent = flag;
      flagsEl.appendChild(li);
    }
  }

  el('scan-results-view').classList.remove('active');
  el('drilldown-view').classList.add('active');
}

function showDrillDownView() {
  el('chart-view').classList.remove('active');
  el('drilldown-view').classList.add('active');
}

async function showChart(symbolId) {
  if (symbolId == null) return;
  const status = el('chart-status');
  status.className = 'status-text';
  status.textContent = 'Loading chart...';

  const date = el('date-input').value;
  const timeframe = el('chart-timeframe').value;

  try {
    const res = await fetch('/api/chart/' + symbolId + '?date=' + encodeURIComponent(date) + '&timeframe=' + encodeURIComponent(timeframe));
    if (!res.ok) throw new Error('Chart request failed: ' + res.status);
    const data = await res.json();

    el('chart-symbol').textContent = data.nseSymbol + ' \u2014 ' + data.timeframe;
    status.textContent = data.candles.length + ' candle(s), ' + data.markers.length + ' marker(s), ' + data.zones.length + ' zone(s).';

    renderChart(data);

    el('drilldown-view').classList.remove('active');
    el('scan-results-view').classList.remove('active');
    el('chart-view').classList.add('active');
  } catch (err) {
    status.className = 'status-text error';
    status.textContent = String(err.message || err);
  }
}

function renderChart(data) {
  const container = el('chart-price-pane');
  container.innerHTML = '';

  if (state.chart && state.chart.api) {
    state.chart.api.remove();
    state.chart = null;
  }

  const chartApi = LightweightCharts.createChart(container, {
    layout: { background: { color: 'transparent' }, textColor: '#7d8798' },
    grid: { vertLines: { color: '#262c3a' }, horzLines: { color: '#262c3a' } },
    rightPriceScale: { borderColor: '#262c3a' },
    timeScale: { borderColor: '#262c3a' },
    crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
    width: container.clientWidth,
    height: container.clientHeight
  });

  const candleSeries = chartApi.addCandlestickSeries({
    upColor: '#2ea043', downColor: '#d0483d', borderVisible: false,
    wickUpColor: '#2ea043', wickDownColor: '#d0483d'
  });

  candleSeries.setData(data.candles.map((c) => ({ time: c.time, open: c.open, high: c.high, low: c.low, close: c.close })));

  candleSeries.setMarkers(data.markers.map((m) => ({
    time: m.time, position: m.position, color: m.color, shape: m.shape, text: m.text
  })));

  state.chart = { api: chartApi, candleSeries: candleSeries, zones: data.zones };

  const redraw = () => renderZones(chartApi, candleSeries, data.zones);
  chartApi.timeScale().subscribeVisibleTimeRangeChange(redraw);
  redraw();

  const onResize = () => {
    if (!state.chart) return;
    chartApi.resize(container.clientWidth, container.clientHeight);
    redraw();
  };
  window.addEventListener('resize', onResize);
}

/**
 * Order Block / FVG zones aren't rectangles Lightweight Charts' base
 * (non-plugin) series API can draw directly — createPriceLine only draws a
 * single full-width horizontal line, and there's no built-in "box" series in
 * the version this dashboard uses without adopting a plugin architecture
 * this vanilla-JS-only dashboard doesn't otherwise need. Instead: convert
 * each zone's time/price bounds to pixel coordinates via the chart's own
 * priceToCoordinate/timeToCoordinate API and position a plain absolutely-
 * positioned <div> over the canvas — the standard technique for this
 * library absent a plugin. Re-run on every visible-range change (pan/zoom)
 * since pixel coordinates are only valid for the currently visible range.
 */
function renderZones(chartApi, candleSeries, zones) {
  const overlay = el('chart-zone-overlay');
  overlay.innerHTML = '';

  const timeScale = chartApi.timeScale();

  for (const zone of zones) {
    const x1 = timeScale.timeToCoordinate(zone.startTime);
    const x2 = zone.endTime ? timeScale.timeToCoordinate(zone.endTime) : null;
    const yTop = candleSeries.priceToCoordinate(zone.upper);
    const yBottom = candleSeries.priceToCoordinate(zone.lower);

    if (x1 == null || yTop == null || yBottom == null) continue; // zone falls outside the currently visible range

    // An unmitigated zone (no endTime) extends to the right edge of the visible chart.
    const rightEdge = overlay.clientWidth;
    const width = Math.max(2, (x2 != null ? x2 : rightEdge) - x1);

    const div = document.createElement('div');
    div.className = 'chart-zone zone-' + zone.kind;
    div.style.left = x1 + 'px';
    div.style.top = Math.min(yTop, yBottom) + 'px';
    div.style.width = width + 'px';
    div.style.height = Math.max(2, Math.abs(yBottom - yTop)) + 'px';
    div.title = zone.kind + ' (' + zone.status + ')';
    overlay.appendChild(div);
  }
}

function showResultsView() {
  el('drilldown-view').classList.remove('active');
  el('scan-results-view').classList.add('active');
}

function tpRow(label, value) {
  return '<div class="tp-label">' + label + '</div><div class="tp-value">' + value + '</div>';
}

function formatDecision(d) {
  return d.replace(/([a-z])([A-Z])/g, '$1 $2');
}

function escapeHtml(s) {
  const div = document.createElement('div');
  div.textContent = s;
  return div.innerHTML;
}

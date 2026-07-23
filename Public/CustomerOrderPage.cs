namespace CafePOS.Api.Public;

/// <summary>
/// The customer-facing QR ordering page: a single self-contained HTML document (inline
/// CSS/JS, no build step, no external requests) served by PublicOrderPageController at
/// GET /order/{token}. It talks to the same origin's token-aware anonymous endpoints
/// (see PublicController) plus the guest-session lifecycle under {apiBase}/session
/// (see GuestSessionController — scan/join/state/cart/order/request-bill/bill). The
/// encrypted token (never the cafe name or table code in plain text) is read
/// client-side from the URL path so this one document serves every table of every cafe.
/// Cookies work here with zero extra config: this page and the API share an origin.
/// </summary>
public static class CustomerOrderPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1" />
<title>Order · CafePOS</title>
<style>
  :root {
    --bg: #F7EFE8;
    --card: #FFFFFF;
    --heading: #2B1810;
    --muted: #8A7364;
    --placeholder: #B5A69B;
    --accent: #C5652E;
    --button: #2B1810;
    --divider: #EDE2D8;
    --input-tint: #F6E9E4;
    --success: #2E7D4F;
    --success-bg: #DCEBDD;
    --danger: #B3261E;
    --danger-bg: #F8D9D3;
    --locked: #8A6D1F;
    --locked-bg: #F6ECC8;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    font-family: -apple-system, Roboto, "Segoe UI", sans-serif;
    background: var(--bg);
    color: var(--heading);
    padding-bottom: 96px;
  }
  .wrap { max-width: 480px; margin: 0 auto; padding: 0 16px; }
  header {
    padding: 20px 16px 14px;
    text-align: center;
  }
  header h1 { margin: 0; font-size: 22px; font-weight: 800; }
  header .table-line { margin-top: 4px; color: var(--muted); font-size: 13px; }
  .banner {
    margin: 0 16px 14px;
    padding: 12px 14px;
    border-radius: 12px;
    font-size: 13px;
    font-weight: 600;
    display: none;
  }
  .banner.show { display: block; }
  .banner.error { background: var(--danger-bg); color: var(--danger); }
  .banner.info { background: var(--input-tint); color: var(--heading); }
  .field-label { font-size: 12px; font-weight: 700; color: var(--muted); margin: 4px 0 6px; }
  .guest-card {
    background: var(--card);
    border-radius: 16px;
    padding: 14px;
    margin-bottom: 18px;
  }
  .guest-row { display: flex; gap: 10px; }
  input[type=text], input[type=tel] {
    flex: 1;
    min-width: 0;
    border: 1px solid var(--divider);
    background: var(--input-tint);
    border-radius: 10px;
    padding: 11px 12px;
    font-size: 14px;
    color: var(--heading);
  }
  input::placeholder { color: var(--placeholder); }
  .cat-title {
    font-size: 12px;
    font-weight: 700;
    letter-spacing: 0.5px;
    color: var(--muted);
    text-transform: uppercase;
    margin: 18px 0 10px;
  }
  .bs-title {
    font-size: 12px;
    font-weight: 700;
    letter-spacing: 0.5px;
    color: var(--muted);
    text-transform: uppercase;
    margin: 4px 0 10px;
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .bs-strip {
    display: flex;
    gap: 10px;
    overflow-x: auto;
    padding: 2px 2px 8px;
    margin-bottom: 8px;
    scroll-snap-type: x proximity;
  }
  .bs-card {
    flex: 0 0 136px;
    scroll-snap-align: start;
    background: var(--card);
    border-radius: 14px;
    padding: 12px;
    border: 1px solid var(--divider);
  }
  .bs-card .item-name { font-size: 13px; line-height: 1.3; min-height: 34px; }
  .bs-card .item-price { margin-top: 6px; }
  .bs-card .stepper { margin-top: 10px; }
  .bs-card .stepper button.add { width: 100%; padding: 8px 0; text-align: center; }
  .bs-card .stepper > .qty { flex: 1; }
  .bs-card.unavailable { opacity: 0.5; }
  .item-card {
    background: var(--card);
    border-radius: 14px;
    padding: 10px;
    margin-bottom: 10px;
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .item-thumb {
    width: 56px; height: 56px; border-radius: 10px; flex: 0 0 56px;
    object-fit: cover; background: var(--input-tint);
  }
  .item-thumb.placeholder {
    display: flex; align-items: center; justify-content: center;
    font-size: 22px;
  }
  .item-info { flex: 1; min-width: 0; }
  .item-name-row { display: flex; align-items: center; gap: 5px; }
  .item-name { font-weight: 700; font-size: 14px; }
  .item-sub { color: var(--muted); font-size: 12px; margin-top: 2px; }
  .item-price { color: var(--heading); font-weight: 700; font-size: 13px; margin-top: 2px; }
  .vnv { width: 12px; height: 12px; border: 1.5px solid; flex: 0 0 auto; display: inline-flex; align-items: center; justify-content: center; }
  .vnv.veg, .vnv.jain { border-color: #0B8043; }
  .vnv.eggetarian { border-color: #B26A00; }
  .vnv.nonveg { border-color: #B71C1C; }
  .vnv .dot { width: 6px; height: 6px; border-radius: 50%; background: #0B8043; }
  .vnv.eggetarian .dot { background: #B26A00; }
  .vnv.nonveg .dot { width: 0; height: 0; border-radius: 0; background: transparent; border-left: 4px solid transparent; border-right: 4px solid transparent; border-bottom: 6px solid #B71C1C; }
  .veg-only-row { display: flex; justify-content: flex-end; padding: 0 14px 6px; }
  .veg-only-toggle { display: inline-flex; align-items: center; gap: 5px; border: 1px solid var(--divider); border-radius: 8px; padding: 5px 10px; background: var(--card); font-size: 12px; font-weight: 700; color: var(--muted); }
  .veg-only-toggle.active { border-color: #0B8043; background: #0B804318; color: #0B8043; }
  .item-lines { margin-top: 6px; }
  .item-line-row { display: flex; align-items: center; gap: 8px; padding: 4px 0; font-size: 12px; }
  .item-line-row .line-label { flex: 1; color: var(--muted); }
  .item-line-row .stepper button { width: 24px; height: 24px; border-radius: 12px; font-size: 15px; }
  .customize-btn {
    background: var(--card); color: var(--accent); padding: 8px 14px;
    border-radius: 10px; font-size: 12px; font-weight: 800; letter-spacing: 0.2px;
    border: 1.5px solid var(--accent); margin-top: 6px; cursor: pointer;
  }
  .opt-overlay {
    position: fixed; inset: 0; background: rgba(43, 24, 16, 0.45);
    display: none; align-items: flex-end; justify-content: center; z-index: 60;
  }
  .opt-overlay.show { display: flex; }
  .opt-sheet {
    background: var(--card); border-radius: 20px 20px 0 0; padding: 20px 18px;
    width: 100%; max-width: 480px; max-height: 82vh; overflow-y: auto;
  }
  .opt-sheet h3 { margin: 0 0 14px; font-size: 17px; }
  .opt-group-title { font-size: 11px; font-weight: 800; letter-spacing: 0.4px; color: var(--muted); text-transform: uppercase; margin: 14px 0 6px; }
  .opt-row { display: flex; align-items: center; gap: 10px; padding: 9px 0; cursor: pointer; }
  .opt-row .opt-label { flex: 1; font-size: 14px; font-weight: 600; }
  .opt-row .opt-price { font-size: 12px; color: var(--muted); font-weight: 700; }
  .opt-radio, .opt-check { width: 19px; height: 19px; border: 2px solid var(--divider); flex: 0 0 auto; display: flex; align-items: center; justify-content: center; }
  .opt-radio { border-radius: 50%; }
  .opt-check { border-radius: 5px; }
  .opt-radio.active, .opt-check.active { border-color: var(--accent); }
  .opt-radio.active::after { content: ''; width: 9px; height: 9px; border-radius: 50%; background: var(--accent); }
  .opt-check.active { background: var(--accent); color: #fff; font-size: 12px; }
  /* A required group with nothing picked — names what's blocking the disabled Add button. */
  .opt-group-title.missing { color: var(--danger, #C0392B); }
  /* Quantity-type groups: -/+ beside the label so one topping can be ordered several times. */
  .opt-stepper { display: flex; align-items: center; gap: 8px; flex: 0 0 auto; }
  .opt-stepper button {
    width: 26px; height: 26px; border-radius: 13px; border: none;
    background: var(--input-tint); color: var(--heading);
    font-size: 15px; font-weight: 700; cursor: pointer; line-height: 1;
  }
  .opt-stepper .qty { min-width: 14px; text-align: center; font-weight: 700; font-size: 13px; }
  .opt-add-btn {
    width: 100%; background: var(--button); color: #fff; border: none;
    padding: 14px 0; border-radius: 14px; font-size: 14px; font-weight: 700;
    cursor: pointer; margin-top: 16px;
  }
  .opt-add-btn:disabled { opacity: 0.5; cursor: not-allowed; }
  .stepper { display: flex; align-items: center; gap: 10px; flex: 0 0 auto; }
  .stepper button {
    width: 30px; height: 30px; border-radius: 15px; border: none;
    background: var(--input-tint); color: var(--heading);
    font-size: 18px; font-weight: 700; cursor: pointer;
  }
  .stepper button.add {
    background: var(--card); color: var(--accent); width: auto; padding: 8px 14px;
    border-radius: 10px; font-size: 12px; font-weight: 800; letter-spacing: 0.2px;
    border: 1.5px solid var(--accent);
  }
  .stepper .qty { min-width: 16px; text-align: center; font-weight: 700; }
  .unavailable { opacity: 0.5; }
  .unavailable .stepper { display: none; }
  .unavailable-tag { font-size: 10px; font-weight: 700; color: var(--danger); }
  .cart-bar {
    position: fixed; left: 0; right: 0; bottom: 0;
    background: var(--card);
    border-top: 1px solid var(--divider);
    padding: 12px 16px calc(12px + env(safe-area-inset-bottom));
    display: flex; align-items: center; gap: 12px;
  }
  .cart-summary { flex: 1; min-width: 0; }
  .cart-summary .count { font-size: 12px; color: var(--muted); }
  .cart-summary .total { font-size: 18px; font-weight: 800; }
  .place-btn {
    background: var(--button); color: #fff; border: none;
    padding: 14px 22px; border-radius: 14px; font-size: 14px; font-weight: 700;
    cursor: pointer;
  }
  .place-btn:disabled { opacity: 0.5; cursor: default; }
  .secondary-btn {
    background: none; border: 1px solid var(--divider);
    color: var(--heading); padding: 12px 20px; border-radius: 12px;
    font-weight: 700; font-size: 13px; cursor: pointer;
  }
  .center-screen {
    min-height: 70vh; display: flex; flex-direction: column;
    align-items: center; justify-content: center; text-align: center; padding: 24px;
  }
  .center-screen h2 { margin: 12px 0 6px; font-size: 20px; }
  .center-screen p { color: var(--muted); font-size: 14px; margin: 0; }
  .confirm-card {
    background: var(--card); border-radius: 20px; padding: 28px 22px;
    text-align: center; margin-top: 20px;
  }
  .confirm-card .badge {
    width: 56px; height: 56px; border-radius: 28px; background: var(--success-bg);
    color: var(--success); font-size: 28px; display: flex; align-items: center;
    justify-content: center; margin: 0 auto 14px;
  }
  .confirm-card .order-no { color: var(--muted); font-size: 13px; margin-top: 4px; }
  .confirm-card .order-total { font-size: 26px; font-weight: 800; margin: 10px 0; }
  .again-btn { margin-top: 18px; }
  .status-pill {
    display: inline-block; font-size: 10px; font-weight: 800; letter-spacing: 0.3px;
    padding: 3px 8px; border-radius: 8px; background: var(--input-tint); color: var(--muted);
    text-transform: uppercase; margin-top: 3px;
  }
  .status-pill.ready { background: var(--success-bg); color: var(--success); }
  .status-pill.served { background: var(--input-tint); color: var(--muted); }
  .bill-line { display: flex; justify-content: space-between; font-size: 13px; padding: 6px 0; }
  .bill-line.total { font-weight: 800; font-size: 16px; border-top: 1px solid var(--divider); margin-top: 6px; padding-top: 10px; }
  .action-row { display: flex; gap: 10px; margin-top: 18px; }
  .action-row button { flex: 1; }
  .locked-note { background: var(--locked-bg); color: var(--locked); border-radius: 12px; padding: 12px 14px; font-size: 13px; font-weight: 600; margin-top: 14px; }
  #app, #placed-screen, #bill-screen, #join-screen, #staff-assist-screen, #ended-screen, #waiting-screen { display: none; }
  .waiting-spinner {
    width: 40px; height: 40px; border-radius: 50%; margin: 4px auto 4px;
    border: 4px solid var(--divider); border-top-color: var(--accent);
    animation: spin 0.8s linear infinite;
  }
  .processing-overlay {
    position: fixed; inset: 0; background: rgba(43, 24, 16, 0.45);
    display: none; align-items: center; justify-content: center; z-index: 50;
  }
  .processing-overlay.show { display: flex; }
  .spinner {
    width: 40px; height: 40px; border-radius: 50%;
    border: 4px solid rgba(255,255,255,0.35); border-top-color: #fff;
    animation: spin 0.8s linear infinite;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
</style>
</head>
<body>
  <div id="loading" class="center-screen"><p>Loading menu…</p></div>

  <div id="app" class="wrap">
    <header>
      <h1 id="business-name">CafePOS</h1>
      <div class="table-line" id="table-line"></div>
    </header>

    <div id="browse-banner" class="banner info">Browsing only from this code — ask a staff member to seat you at a table to place an order.</div>
    <div id="error-banner" class="banner error"></div>

    <div id="guest-card" class="guest-card">
      <div class="field-label">Your name (optional)</div>
      <div class="guest-row">
        <input type="text" id="guest-name" placeholder="e.g. Priya" maxlength="60" />
      </div>
      <div class="field-label" style="margin-top:12px">Mobile number *</div>
      <div class="guest-row">
        <input type="tel" id="guest-phone" placeholder="e.g. 9876543210" maxlength="10" inputmode="numeric" />
      </div>
    </div>

    <div id="bestsellers-section" style="display:none">
      <div class="bs-title">🔥 Best Sellers</div>
      <div class="bs-strip" id="bestsellers-strip"></div>
    </div>

    <div class="veg-only-row">
      <button type="button" class="veg-only-toggle" id="veg-only-toggle">
        <span class="vnv veg"><span class="dot"></span></span>
        Veg Only
      </button>
    </div>
    <div id="menu-root"></div>
  </div>

  <div id="join-screen" class="center-screen">
    <h2>Someone's already ordering at this table</h2>
    <p>Are you sitting with them? Join their order and see the same cart.</p>
    <button class="place-btn again-btn" id="join-btn">Yes, I'm at this table</button>
  </div>

  <div id="staff-assist-screen" class="center-screen">
    <h2>Please call a staff member</h2>
    <p>There's already a bill open on this table that isn't linked to a live session — a staff member can help you continue or start fresh.</p>
  </div>

  <div id="ended-screen" class="center-screen">
    <h2 id="ended-title">Session ended</h2>
    <p id="ended-message">Please scan the QR code on your table again.</p>
  </div>

  <div id="waiting-screen" class="center-screen">
    <div class="waiting-spinner"></div>
    <h2>Order sent — waiting for staff to confirm</h2>
    <p>Someone from the team will confirm your order in a moment before it goes to the kitchen.</p>
  </div>

  <div id="placed-screen" class="wrap">
    <header><h1 id="placed-business-name">CafePOS</h1><div class="table-line" id="placed-table-line"></div></header>
    <div class="confirm-card">
      <div class="badge">✓</div>
      <h2>Order sent to the kitchen!</h2>
      <div id="placed-items"></div>
      <div class="order-total" id="placed-total"></div>
      <div class="action-row">
        <button class="secondary-btn" id="add-more-btn">Add more items</button>
        <button class="place-btn" id="request-bill-btn">Request Bill</button>
      </div>
    </div>
  </div>

  <div id="bill-screen" class="wrap">
    <header><h1 id="bill-business-name">CafePOS</h1><div class="table-line" id="bill-table-line"></div></header>
    <div class="confirm-card">
      <h2>Your Bill</h2>
      <div id="bill-lines" style="text-align:left"></div>
      <div class="locked-note">Bill requested — ordering is closed. Please pay at the counter.</div>
    </div>
  </div>

  <div id="processing-overlay" class="processing-overlay"><div class="spinner"></div></div>

  <div id="opt-overlay" class="opt-overlay">
    <div class="opt-sheet">
      <h3 id="opt-item-name"></h3>
      <div id="opt-body"></div>
      <button class="opt-add-btn" id="opt-add-btn">Add to Order</button>
    </div>
  </div>

  <div id="cart-bar" class="cart-bar" style="display:none">
    <div class="cart-summary">
      <div class="count" id="cart-count">0 items</div>
      <div class="total" id="cart-total">₹0.00</div>
    </div>
    <button class="place-btn" id="place-btn">Place Order</button>
  </div>

<script>
(function () {
  var pathParts = location.pathname.split('/').filter(Boolean); // ['order', token]
  var token = decodeURIComponent(pathParts[1] || '');
  var apiBase = '/api/public/' + encodeURIComponent(token);
  var sessionBase = apiBase + '/session';
  var state = {
    table: null, menu: [], bestSellers: [], taxRatePct: 8, cart: {}, browseOnly: false,
    order: null, // last known OrderDto from the server (session-scoped), or null
    vegOnly: false,
  };
  var pollTimer = null;

  function el(tag, cls, html) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (html !== undefined) e.innerHTML = html;
    return e;
  }

  function money(n) { return '₹' + n.toFixed(2); }

  function showError(msg) {
    var b = document.getElementById('error-banner');
    b.textContent = msg;
    b.classList.add('show');
  }

  function clearError() {
    var b = document.getElementById('error-banner');
    b.classList.remove('show');
    b.textContent = '';
  }

  function fetchJson(url, options) {
    return fetch(url, options).then(function (res) {
      return res.json().catch(function () { return null; }).then(function (body) {
        if (!res.ok) {
          var message = (body && (body.title || body.detail)) || ('Request failed (' + res.status + ')');
          var err = new Error(message);
          err.status = res.status;
          throw err;
        }
        return body;
      });
    });
  }

  function stopPolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
  }

  function startPolling() {
    stopPolling();
    pollTimer = setInterval(function () {
      fetchJson(sessionBase + '/state').then(handleStateUpdate).catch(function (err) {
        if (err.status === 410) showEnded('This session has ended.');
      });
    }, 5000);
  }

  // Applied both right after an action and on each poll tick — the session's own status
  // (LOCKED from another device requesting the bill, etc.) always wins over whatever
  // screen we were already showing.
  function handleStateUpdate(s) {
    state.order = s.order;
    if (s.status === 'LOCKED') { showBillScreen(s); return; }
    if (s.order && s.order.pendingStaffConfirmation) { showWaitingScreen(); return; }
    if (s.order && s.order.currentFireBatch > 0) { showPlacedScreen(s); return; }
    syncCartFromOrder();
    renderMenu();
    renderBestSellers();
    renderCartBar();
  }

  function hideAllScreens() {
    ['app', 'join-screen', 'staff-assist-screen', 'ended-screen', 'waiting-screen', 'placed-screen', 'bill-screen'].forEach(function (id) {
      document.getElementById(id).style.display = 'none';
    });
    document.getElementById('cart-bar').style.display = 'none';
    document.getElementById('loading').style.display = 'none';
  }

  function showEnded(message) {
    stopPolling();
    hideAllScreens();
    document.getElementById('ended-message').textContent = message;
    document.getElementById('ended-screen').style.display = 'flex';
  }

  function showMenuScreen() {
    hideAllScreens();
    document.getElementById('app').style.display = 'block';
    renderMenu();
    renderBestSellers();
    renderCartBar();
    startPolling();
  }

  // Staff-Confirm Mode: shown once Place Order is tapped while the order still hasn't
  // fired (currentFireBatch === 0, same "unfired" signal as an empty cart) but the server
  // has flagged it pendingStaffConfirmation — i.e. it's been submitted and is sitting with
  // the floor staff, not silently stuck. Polling (already running) flips to showPlacedScreen
  // automatically the moment a staff member confirms and it actually fires.
  function showWaitingScreen() {
    hideAllScreens();
    document.getElementById('waiting-screen').style.display = 'flex';
    startPolling();
  }

  function showPlacedScreen(s) {
    hideAllScreens();
    document.getElementById('placed-business-name').textContent = document.getElementById('business-name').textContent;
    document.getElementById('placed-table-line').textContent = document.getElementById('table-line').textContent;
    var itemsEl = document.getElementById('placed-items');
    itemsEl.innerHTML = '';
    (s.order.items || []).forEach(function (item) {
      var row = el('div', null);
      row.style.cssText = 'display:flex;justify-content:space-between;align-items:center;padding:4px 0;text-align:left';
      var desc = lineDescriptor(item);
      var label = el('span', null, item.qty + '× ' + item.name + (desc ? ' (' + desc + ')' : ''));
      var badge = el('span', 'status-pill' + (item.status === 'READY' ? ' ready' : item.status === 'SERVED' ? ' served' : ''), item.status);
      row.appendChild(label);
      row.appendChild(badge);
      itemsEl.appendChild(row);
    });
    document.getElementById('placed-total').textContent = money(s.order.total);
    document.getElementById('placed-screen').style.display = 'block';
    startPolling();
  }

  function showBillScreen(s) {
    hideAllScreens();
    document.getElementById('bill-business-name').textContent = document.getElementById('business-name').textContent;
    document.getElementById('bill-table-line').textContent = document.getElementById('table-line').textContent;
    var linesEl = document.getElementById('bill-lines');
    linesEl.innerHTML = '';
    if (s.order) {
      (s.order.items || []).forEach(function (item) {
        var row = el('div', 'bill-line');
        var desc = lineDescriptor(item);
        row.appendChild(el('span', null, item.qty + '× ' + item.name + (desc ? ' (' + desc + ')' : '')));
        row.appendChild(el('span', null, money(item.price * item.qty)));
        linesEl.appendChild(row);
      });
      linesEl.appendChild((function () { var r = el('div', 'bill-line'); r.appendChild(el('span', null, 'Tax')); r.appendChild(el('span', null, money(s.order.tax))); return r; })());
      linesEl.appendChild((function () { var r = el('div', 'bill-line total'); r.appendChild(el('span', null, 'Total')); r.appendChild(el('span', null, money(s.order.total))); return r; })());
    }
    document.getElementById('bill-screen').style.display = 'block';
    // Ordering is closed once LOCKED — no further polling needed; staff settling the
    // bill (OrdersController.Pay) is what ends the session from here.
    stopPolling();
  }

  function syncCartFromOrder() {
    state.cart = {};
    if (!state.order) return;
    // Sums (not overwrites) so an item ordered as two different combos — e.g. "Half" and
    // "Full + Extra Cheese" — still contributes its full total count here, even though this
    // flat map can't represent WHICH combos; renderMenu() below only uses this map for
    // items with no variants/modifiers, which can only ever have one line anyway.
    (state.order.items || []).filter(function (i) { return i.fireBatch === 0 && !i.voided; }).forEach(function (i) {
      state.cart[i.menuItemId] = (state.cart[i.menuItemId] || 0) + i.qty;
    });
  }

  // Every unfired (not-yet-sent-to-kitchen) line for one menu item — a customer can have
  // several at once (different Half/Full or topping combos), unlike the flat state.cart map.
  function linesForItem(menuItemId) {
    if (!state.order) return [];
    return (state.order.items || []).filter(function (i) {
      return i.menuItemId === menuItemId && i.fireBatch === 0 && !i.voided;
    });
  }

  function vnvBadge(type) {
    if (!type) return null;
    var cls = type === 'NonVeg' ? 'nonveg' : type === 'Eggetarian' ? 'eggetarian' : type === 'Jain' ? 'jain' : 'veg';
    var badge = el('span', 'vnv ' + cls);
    badge.appendChild(el('span', 'dot'));
    return badge;
  }

  /** A server line's add-ons flattened back to the wire format: one id per UNIT, so a
   * qty-2 selection round-trips as the same id twice (see CreateOrderItemDto). Every
   * re-send and line-identity check must go through this — mapping straight to
   * modifierOptionId would silently drop a "2x Extra Cheese" back down to 1x. */
  function lineOptionIds(line) {
    var ids = [];
    (line.selectedModifiers || []).forEach(function (m) {
      for (var i = 0; i < (m.qty || 1); i++) ids.push(m.modifierOptionId);
    });
    return ids;
  }

  function lineDescriptor(line) {
    var parts = [];
    if (line.variantName) parts.push(line.variantName);
    (line.selectedModifiers || []).forEach(function (m) {
      parts.push((m.qty || 1) > 1 ? (m.qty + 'x ' + m.name) : m.name);
    });
    return parts.join(', ');
  }

  // Non-veg/eggetarian hidden, everything else (veg, jain, and untagged items — hiding
  // an untagged item would be guessing it's non-veg rather than reading an actual tag)
  // stays visible. Mirrors the POS ordering screen's own Veg Only filter.
  function visibleMenu() {
    if (!state.vegOnly) return state.menu;
    return state.menu.filter(function (m) {
      return !m.vegNonVegType || m.vegNonVegType === 'Veg' || m.vegNonVegType === 'Jain';
    });
  }

  function renderMenu() {
    var root = document.getElementById('menu-root');
    root.innerHTML = '';
    var menu = visibleMenu();
    var categories = [];
    menu.forEach(function (item) {
      if (categories.indexOf(item.category) === -1) categories.push(item.category);
    });

    categories.forEach(function (cat) {
      root.appendChild(el('div', 'cat-title', cat));
      menu.filter(function (m) { return m.category === cat; }).forEach(function (item) {
        var hasOptions = (item.variants && item.variants.length > 0) || (item.modifiers && item.modifiers.length > 0);
        var qty = state.cart[item.id] || 0;
        var card = el('div', 'item-card' + (item.available ? '' : ' unavailable'));

        if (item.image) {
          var img = document.createElement('img');
          img.className = 'item-thumb';
          img.src = item.image;
          img.alt = item.name;
          card.appendChild(img);
        } else {
          card.appendChild(el('div', 'item-thumb placeholder', '🍽️'));
        }

        var info = el('div', 'item-info');
        var nameRow = el('div', 'item-name-row');
        var badge = vnvBadge(item.vegNonVegType);
        if (badge) nameRow.appendChild(badge);
        nameRow.appendChild(el('div', 'item-name', item.name));
        info.appendChild(nameRow);
        if (item.subtitle) info.appendChild(el('div', 'item-sub', item.subtitle));
        info.appendChild(el('div', 'item-price', (hasOptions ? 'from ' : '') + money(hasOptions ? Math.min.apply(null, [item.price].concat(item.variants.map(function (v) { return v.price; }))) : item.price)));
        if (!item.available) info.appendChild(el('div', 'unavailable-tag', 'CURRENTLY UNAVAILABLE'));

        // Existing combos of this item already in the cart, each with its own stepper —
        // only meaningful once an item can have more than one distinct line (variant/topping
        // picks), which a plain item never does.
        if (!state.browseOnly && hasOptions) {
          var lines = linesForItem(item.id);
          if (lines.length > 0) {
            var linesBox = el('div', 'item-lines');
            lines.forEach(function (line) {
              var row = el('div', 'item-line-row');
              row.appendChild(el('span', 'line-label', line.qty + '× ' + (lineDescriptor(line) || 'Regular')));
              var stepper = el('div', 'stepper');
              var minus = el('button', null, '−');
              minus.onclick = function () { changeLineQty(item.id, line.variantId, lineOptionIds(line), line.qty - 1); };
              var qtyEl = el('span', 'qty', String(line.qty));
              var plus = el('button', null, '+');
              plus.onclick = function () { changeLineQty(item.id, line.variantId, lineOptionIds(line), line.qty + 1); };
              stepper.appendChild(minus);
              stepper.appendChild(qtyEl);
              stepper.appendChild(plus);
              row.appendChild(stepper);
              linesBox.appendChild(row);
            });
            info.appendChild(linesBox);
          }
        }
        card.appendChild(info);

        // Ordering is a table-only feature — the no-table "general menu" code (see
        // TablesController.GetMenuOnlyQrToken) is for browsing prices/availability
        // only, so it never gets an Add/quantity stepper at all.
        if (!state.browseOnly) {
          if (hasOptions) {
            var customize = el('button', 'customize-btn', 'Customize');
            customize.onclick = function () { openItemOptions(item); };
            card.appendChild(customize);
          } else {
            var stepper = el('div', 'stepper');
            if (qty > 0) {
              var minus = el('button', null, '−');
              minus.onclick = function () { changeQty(item.id, -1); };
              var qtyEl = el('span', 'qty', String(qty));
              var plus = el('button', null, '+');
              plus.onclick = function () { changeQty(item.id, 1); };
              stepper.appendChild(minus);
              stepper.appendChild(qtyEl);
              stepper.appendChild(plus);
            } else {
              var add = el('button', 'add', 'Add');
              add.onclick = function () { changeQty(item.id, 1); };
              stepper.appendChild(add);
            }
            card.appendChild(stepper);
          }
        }
        root.appendChild(card);
      });
    });
  }

  function renderBestSellers() {
    var section = document.getElementById('bestsellers-section');
    if (!state.bestSellers.length) { section.style.display = 'none'; return; }
    section.style.display = 'block';
    var strip = document.getElementById('bestsellers-strip');
    strip.innerHTML = '';
    state.bestSellers.forEach(function (item) {
      var qty = state.cart[item.id] || 0;
      var card = el('div', 'bs-card' + (item.available ? '' : ' unavailable'));
      card.appendChild(el('div', 'item-name', item.name));
      card.appendChild(el('div', 'item-price', money(item.price)));

      if (!state.browseOnly) {
        var stepper = el('div', 'stepper');
        if (qty > 0) {
          var minus = el('button', null, '−');
          minus.onclick = function () { changeQty(item.id, -1); };
          var qtyEl = el('span', 'qty', String(qty));
          var plus = el('button', null, '+');
          plus.onclick = function () { changeQty(item.id, 1); };
          stepper.appendChild(minus);
          stepper.appendChild(qtyEl);
          stepper.appendChild(plus);
        } else {
          var add = el('button', 'add', 'Add');
          add.onclick = function () { changeQty(item.id, 1); };
          stepper.appendChild(add);
        }
        card.appendChild(stepper);
      }
      strip.appendChild(card);
    });
  }

  // Every tap immediately calls the server (the cart lives session-side, not just in
  // this tab — see GuestSessionController.AddCartItem) and re-syncs from its response,
  // rather than trusting local arithmetic. variantId/modifierOptionIds identify WHICH
  // line to upsert — the backend treats (menuItem, variant, exact option set) as the
  // line's identity (see OrderBuildingService.AddOrUpdateCartItemAsync), so an existing
  // line's qty change must resend the exact same combo, not just the menuItemId.
  function changeLineQty(menuItemId, variantId, modifierOptionIds, nextQty) {
    clearError();
    var next = Math.max(0, nextQty);
    var phoneDigits = (document.getElementById('guest-phone').value || '').replace(/\D/g, '');
    if (!state.order && phoneDigits.length !== 10) {
      showError('Enter a valid 10-digit mobile number before adding items.');
      document.getElementById('guest-phone').focus();
      return;
    }

    fetchJson(sessionBase + '/cart/items', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        menuItemId: menuItemId,
        qty: next,
        modifier: null,
        variantId: variantId || null,
        modifierOptionIds: (modifierOptionIds && modifierOptionIds.length) ? modifierOptionIds : null,
        guestName: document.getElementById('guest-name').value || null,
        guestPhone: phoneDigits || null,
      }),
    }).then(function (s) {
      state.order = s.order;
      syncCartFromOrder();
      if (state.order) document.getElementById('guest-card').style.display = 'none';
      renderMenu();
      renderBestSellers();
      renderCartBar();
    }).catch(function (err) {
      if (err.status === 410) { showEnded('This session has ended.'); return; }
      if (err.status === 423) { showBillScreen({ order: state.order }); return; }
      showError(err.message);
    });
  }

  function changeQty(menuItemId, delta) {
    changeLineQty(menuItemId, null, [], (state.cart[menuItemId] || 0) + delta);
  }

  // ---------- Item options picker (Half/Full variant + toppings) ----------
  var optState = { item: null, variantId: null, selectedOptionIds: [] };

  /** selectedOptionIds holds one entry per UNIT — a Quantity group repeats the same id —
   * so this expands to one option object per unit and the price total counts each. */
  function optSelectedOptions() {
    if (!optState.item) return [];
    var all = [];
    optState.item.modifiers.forEach(function (m) { all = all.concat(m.options); });
    return optState.selectedOptionIds.map(function (id) {
      return all.find(function (o) { return o.id === id; });
    }).filter(function (o) { return !!o; });
  }

  function optOptionQty(optionId) {
    return optState.selectedOptionIds.filter(function (id) { return id === optionId; }).length;
  }

  /** Required groups with nothing picked. The backend rejects these outright
   * (OrderBuildingService.ResolveLinePricingAsync), so the button blocks first and names
   * what's missing rather than letting the customer hit a raw validation error. */
  function optMissingRequired() {
    if (!optState.item) return [];
    return optState.item.modifiers.filter(function (m) {
      return m.isRequired && !m.options.some(function (o) { return optState.selectedOptionIds.indexOf(o.id) !== -1; });
    });
  }

  function renderItemOptions() {
    var item = optState.item;
    document.getElementById('opt-item-name').textContent = item.name;
    var body = document.getElementById('opt-body');
    body.innerHTML = '';

    if (item.variants.length > 0) {
      body.appendChild(el('div', 'opt-group-title', 'Size'));
      item.variants.forEach(function (v) {
        var active = v.id === optState.variantId;
        var row = el('div', 'opt-row');
        var radio = el('span', 'opt-radio' + (active ? ' active' : ''));
        row.appendChild(radio);
        row.appendChild(el('span', 'opt-label', v.name));
        row.appendChild(el('span', 'opt-price', money(v.price)));
        row.onclick = function () { optState.variantId = v.id; renderItemOptions(); };
        body.appendChild(row);
      });
    }

    var missing = optMissingRequired();
    item.modifiers.forEach(function (m) {
      var groupMissing = missing.indexOf(m) !== -1;
      var title = el('div', 'opt-group-title' + (groupMissing ? ' missing' : ''), m.name + (m.isRequired ? ' · Required' : ''));
      body.appendChild(title);
      var isRadio = m.type === 'Radio';
      var isQuantity = m.type === 'Quantity';
      m.options.forEach(function (o) {
        var qty = optOptionQty(o.id);
        var active = qty > 0;
        var row = el('div', 'opt-row');
        var mark = el('span', (isRadio ? 'opt-radio' : 'opt-check') + (active ? ' active' : ''));
        if (!isRadio && active) mark.textContent = isQuantity ? String(qty) : '✓';
        row.appendChild(mark);
        row.appendChild(el('span', 'opt-label', o.name));
        // A Quantity group gets its own −/+ pair so the same topping can be ordered
        // multiple times; tapping the row elsewhere just seeds the first unit.
        if (isQuantity && active) {
          var stepper = el('div', 'opt-stepper');
          var dec = el('button', null, '−');
          dec.onclick = function (e) {
            e.stopPropagation();
            var at = optState.selectedOptionIds.indexOf(o.id);
            if (at !== -1) optState.selectedOptionIds.splice(at, 1);
            renderItemOptions();
          };
          var inc = el('button', null, '+');
          inc.onclick = function (e) {
            e.stopPropagation();
            optState.selectedOptionIds = optState.selectedOptionIds.concat([o.id]);
            renderItemOptions();
          };
          stepper.appendChild(dec);
          stepper.appendChild(el('span', 'qty', String(qty)));
          stepper.appendChild(inc);
          row.appendChild(stepper);
        }
        row.appendChild(el('span', 'opt-price', o.price === 0 ? 'Free' : ('+' + money(o.price))));
        row.onclick = function () {
          if (isRadio) {
            var siblingIds = m.options.map(function (x) { return x.id; });
            optState.selectedOptionIds = optState.selectedOptionIds.filter(function (id) { return siblingIds.indexOf(id) === -1; }).concat([o.id]);
          } else if (isQuantity) {
            if (!active) optState.selectedOptionIds = optState.selectedOptionIds.concat([o.id]);
          } else if (active) {
            optState.selectedOptionIds = optState.selectedOptionIds.filter(function (id) { return id !== o.id; });
          } else {
            optState.selectedOptionIds = optState.selectedOptionIds.concat([o.id]);
          }
          renderItemOptions();
        };
        body.appendChild(row);
      });
    });

    var variant = item.variants.find(function (v) { return v.id === optState.variantId; });
    var unitPrice = (variant ? variant.price : item.price) + optSelectedOptions().reduce(function (s, o) { return s + o.price; }, 0);
    var addBtn = document.getElementById('opt-add-btn');
    addBtn.textContent = missing.length > 0
      ? ('Choose ' + missing.map(function (m) { return m.name; }).join(', '))
      : ('Add to Order — ' + money(unitPrice));
    addBtn.disabled = missing.length > 0;
  }

  function openItemOptions(item) {
    optState.item = item;
    optState.variantId = (item.variants.find(function (v) { return v.isDefault; }) || item.variants[0] || {}).id || null;
    optState.selectedOptionIds = [];
    renderItemOptions();
    document.getElementById('opt-overlay').classList.add('show');
  }

  function closeItemOptions() {
    document.getElementById('opt-overlay').classList.remove('show');
    optState.item = null;
  }

  function confirmItemOptions() {
    if (!optState.item) return;
    if (optMissingRequired().length > 0) return;
    var item = optState.item;
    var sortedIds = optState.selectedOptionIds.slice().sort(function (a, b) { return a - b; });
    var existing = linesForItem(item.id).find(function (line) {
      var lineIds = lineOptionIds(line).sort(function (a, b) { return a - b; });
      return line.variantId === optState.variantId && lineIds.length === sortedIds.length && lineIds.every(function (id, i) { return id === sortedIds[i]; });
    });
    var nextQty = (existing ? existing.qty : 0) + 1;
    changeLineQty(item.id, optState.variantId, sortedIds, nextQty);
    closeItemOptions();
  }

  document.getElementById('opt-add-btn').onclick = confirmItemOptions;
  document.getElementById('opt-overlay').onclick = function (e) { if (e.target.id === 'opt-overlay') closeItemOptions(); };

  function cartCount() {
    return unfiredLines().reduce(function (sum, i) { return sum + i.qty; }, 0);
  }

  function unfiredLines() {
    if (!state.order) return [];
    return (state.order.items || []).filter(function (i) { return i.fireBatch === 0 && !i.voided; });
  }

  // Sums each line's already-correct effective price (base or variant, plus every
  // selected topping) — reading straight from the server's OrderItem.Price rather than
  // recomputing from state.menu's base price, which would silently ignore variant/topping
  // deltas (the bug this whole feature exists to fix).
  function cartSubtotal() {
    return unfiredLines().reduce(function (sum, i) { return sum + i.price * i.qty; }, 0);
  }

  function renderCartBar() {
    var bar = document.getElementById('cart-bar');
    var count = cartCount();
    if (count === 0) { bar.style.display = 'none'; return; }
    bar.style.display = 'flex';
    var subtotal = cartSubtotal();
    var tax = subtotal * (state.taxRatePct / 100);
    document.getElementById('cart-count').textContent = count + (count === 1 ? ' item' : ' items') + ' · incl. tax';
    document.getElementById('cart-total').textContent = money(subtotal + tax);
  }

  function placeOrder() {
    if (cartCount() === 0) return;
    clearError();

    var btn = document.getElementById('place-btn');
    btn.disabled = true;
    btn.textContent = 'Sending…';
    document.getElementById('processing-overlay').classList.add('show');

    fetchJson(sessionBase + '/order', { method: 'POST' }).then(function (s) {
      document.getElementById('processing-overlay').classList.remove('show');
      state.order = s.order;
      if (s.order && s.order.pendingStaffConfirmation) { showWaitingScreen(); return; }
      showPlacedScreen(s);
    }).catch(function (err) {
      document.getElementById('processing-overlay').classList.remove('show');
      btn.disabled = false;
      btn.textContent = 'Place Order';
      if (err.status === 410) { showEnded('This session has ended.'); return; }
      showError(err.message);
    });
  }

  function requestBill() {
    fetchJson(sessionBase + '/request-bill', { method: 'POST' }).then(function (s) {
      showBillScreen(s);
    }).catch(function (err) {
      if (err.status === 410) { showEnded('This session has ended.'); return; }
      showError(err.message);
    });
  }

  function doScan() {
    fetchJson(sessionBase + '/scan', { method: 'POST' }).then(function (result) {
      document.getElementById('loading').style.display = 'none';
      if (result.case === 'STAFF_ASSIST') { hideAllScreens(); document.getElementById('staff-assist-screen').style.display = 'flex'; return; }
      if (result.case === 'JOIN') { hideAllScreens(); document.getElementById('join-screen').style.display = 'flex'; return; }
      state.order = result.state.order;
      if (result.case === 'BILL_LOCKED') { showBillScreen(result.state); return; }
      syncCartFromOrder();
      if (state.order) document.getElementById('guest-card').style.display = 'none';
      if (state.order && state.order.pendingStaffConfirmation) { showWaitingScreen(); return; }
      if (state.order && state.order.currentFireBatch > 0) { showPlacedScreen(result.state); return; }
      showMenuScreen();
    }).catch(function () {
      document.getElementById('loading').innerHTML =
        '<h2>Something went wrong</h2><p>Please re-scan the QR code on your table, or ask a staff member for help.</p>';
    });
  }

  function joinSession() {
    fetchJson(sessionBase + '/join', { method: 'POST' }).then(function (s) {
      state.order = s.order;
      syncCartFromOrder();
      if (state.order) document.getElementById('guest-card').style.display = 'none';
      if (state.order && state.order.pendingStaffConfirmation) { showWaitingScreen(); return; }
      if (state.order && state.order.currentFireBatch > 0) { showPlacedScreen(s); return; }
      showMenuScreen();
    }).catch(function (err) {
      showError(err.message);
      hideAllScreens();
      document.getElementById('app').style.display = 'block';
    });
  }

  function init() {
    if (!token) {
      document.getElementById('loading').innerHTML =
        '<h2>Missing table code</h2><p>Please re-scan the QR code on your table.</p>';
      return;
    }

    Promise.all([
      fetchJson(apiBase + '/table'),
      fetchJson(apiBase + '/menu-items'),
      fetchJson(apiBase + '/settings'),
      fetchJson(apiBase + '/best-sellers').catch(function () { return []; }),
    ]).then(function (results) {
      state.table = results[0];
      state.menu = results[1];
      state.taxRatePct = results[2].taxRatePct;
      state.bestSellers = results[3];

      document.getElementById('business-name').textContent = results[2].businessName || 'CafePOS';
      state.browseOnly = !state.table.code;
      document.getElementById('table-line').textContent = state.table.code
        ? ('Table ' + state.table.code + ' · ' + state.table.seats + ' seats')
        : 'Browsing the menu';
      document.getElementById('place-btn').onclick = placeOrder;
      document.getElementById('join-btn').onclick = joinSession;
      document.getElementById('add-more-btn').onclick = showMenuScreen;
      document.getElementById('request-bill-btn').onclick = requestBill;
      document.getElementById('veg-only-toggle').onclick = function () {
        state.vegOnly = !state.vegOnly;
        document.getElementById('veg-only-toggle').classList.toggle('active', state.vegOnly);
        renderMenu();
      };

      if (state.browseOnly) {
        // No table = no session (see GuestSessionController.ResolveAsync) — browsing
        // only, exactly as before this feature existed.
        document.getElementById('browse-banner').classList.add('show');
        document.getElementById('guest-card').style.display = 'none';
        showMenuScreen();
        return;
      }

      doScan();
    }).catch(function () {
      document.getElementById('loading').innerHTML =
        '<h2>Table not found</h2><p>This QR code doesn\'t match an open table. Please ask a staff member for help.</p>';
    });
  }

  init();
})();
</script>
</body>
</html>
""";
}

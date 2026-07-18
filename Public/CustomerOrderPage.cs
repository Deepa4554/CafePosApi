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
  .item-name { font-weight: 700; font-size: 14px; }
  .item-sub { color: var(--muted); font-size: 12px; margin-top: 2px; }
  .item-price { color: var(--heading); font-weight: 700; font-size: 13px; margin-top: 2px; }
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
  #app, #placed-screen, #bill-screen, #join-screen, #staff-assist-screen, #ended-screen { display: none; }
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
    if (s.order && s.order.currentFireBatch > 0) { showPlacedScreen(s); return; }
    syncCartFromOrder();
    renderMenu();
    renderBestSellers();
    renderCartBar();
  }

  function hideAllScreens() {
    ['app', 'join-screen', 'staff-assist-screen', 'ended-screen', 'placed-screen', 'bill-screen'].forEach(function (id) {
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

  function showPlacedScreen(s) {
    hideAllScreens();
    document.getElementById('placed-business-name').textContent = document.getElementById('business-name').textContent;
    document.getElementById('placed-table-line').textContent = document.getElementById('table-line').textContent;
    var itemsEl = document.getElementById('placed-items');
    itemsEl.innerHTML = '';
    (s.order.items || []).forEach(function (item) {
      var row = el('div', null);
      row.style.cssText = 'display:flex;justify-content:space-between;align-items:center;padding:4px 0;text-align:left';
      var label = el('span', null, item.qty + '× ' + item.name);
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
        row.appendChild(el('span', null, item.qty + '× ' + item.name));
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
    (state.order.items || []).filter(function (i) { return i.fireBatch === 0; }).forEach(function (i) {
      state.cart[i.menuItemId] = i.qty;
    });
  }

  function renderMenu() {
    var root = document.getElementById('menu-root');
    root.innerHTML = '';
    var categories = [];
    state.menu.forEach(function (item) {
      if (categories.indexOf(item.category) === -1) categories.push(item.category);
    });

    categories.forEach(function (cat) {
      root.appendChild(el('div', 'cat-title', cat));
      state.menu.filter(function (m) { return m.category === cat; }).forEach(function (item) {
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
        info.appendChild(el('div', 'item-name', item.name));
        if (item.subtitle) info.appendChild(el('div', 'item-sub', item.subtitle));
        info.appendChild(el('div', 'item-price', money(item.price)));
        if (!item.available) info.appendChild(el('div', 'unavailable-tag', 'CURRENTLY UNAVAILABLE'));
        card.appendChild(info);

        // Ordering is a table-only feature — the no-table "general menu" code (see
        // TablesController.GetMenuOnlyQrToken) is for browsing prices/availability
        // only, so it never gets an Add/quantity stepper at all.
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
  // rather than trusting local arithmetic.
  function changeQty(menuItemId, delta) {
    clearError();
    var next = Math.max(0, (state.cart[menuItemId] || 0) + delta);
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

  function cartCount() {
    var count = 0;
    Object.keys(state.cart).forEach(function (id) { count += state.cart[id]; });
    return count;
  }

  function cartSubtotal() {
    var sum = 0;
    Object.keys(state.cart).forEach(function (id) {
      var item = state.menu.find(function (m) { return m.id === Number(id); });
      if (item) sum += item.price * state.cart[id];
    });
    return sum;
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

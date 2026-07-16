namespace CafePOS.Api.Public;

/// <summary>
/// The customer-facing QR ordering page: a single self-contained HTML document (inline
/// CSS/JS, no build step, no external requests) served by PublicOrderPageController at
/// GET /order/{token}. It talks to the same origin's token-aware anonymous endpoints
/// (see PublicController) and posts the order to POST /api/orders/public/{token}. The
/// encrypted token (never the cafe name or table code in plain text) is read
/// client-side from the URL path so this one document serves every table of every cafe.
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
  .banner.occupied { background: #F6ECC8; color: #8A6D1F; }
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
  .again-btn {
    margin-top: 18px; background: none; border: 1px solid var(--divider);
    color: var(--heading); padding: 12px 20px; border-radius: 12px;
    font-weight: 700; font-size: 13px; cursor: pointer;
  }
  .whatsapp-btn {
    margin-top: 12px; width: 100%; background: #25D366; color: #fff; border: none;
    padding: 13px 20px; border-radius: 12px; font-weight: 700; font-size: 13px;
    cursor: pointer; display: flex; align-items: center; justify-content: center; gap: 8px;
  }
  #app { display: none; }
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

    <div id="occupied-banner" class="banner occupied">This table already has an order in progress. You can still browse — ask a staff member if you'd like to add to the existing order.</div>
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

  <div id="confirm-root"></div>

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
  var state = { table: null, menu: [], bestSellers: [], taxRatePct: 8, cart: {}, browseOnly: false };

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
          throw new Error(message);
        }
        return body;
      });
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

  function changeQty(menuItemId, delta) {
    var next = (state.cart[menuItemId] || 0) + delta;
    if (next <= 0) delete state.cart[menuItemId];
    else state.cart[menuItemId] = next;
    renderMenu();
    renderBestSellers();
    renderCartBar();
  }

  function cartLines() {
    return Object.keys(state.cart).map(function (id) {
      var item = state.menu.find(function (m) { return m.id === Number(id); });
      return { item: item, qty: state.cart[id] };
    }).filter(function (l) { return l.item; });
  }

  function renderCartBar() {
    var lines = cartLines();
    var bar = document.getElementById('cart-bar');
    if (lines.length === 0) { bar.style.display = 'none'; return; }
    bar.style.display = 'flex';
    var count = lines.reduce(function (sum, l) { return sum + l.qty; }, 0);
    var subtotal = lines.reduce(function (sum, l) { return sum + l.qty * l.item.price; }, 0);
    var tax = subtotal * (state.taxRatePct / 100);
    document.getElementById('cart-count').textContent = count + (count === 1 ? ' item' : ' items') + ' · incl. tax';
    document.getElementById('cart-total').textContent = money(subtotal + tax);
  }

  function placeOrder() {
    var lines = cartLines();
    if (lines.length === 0) return;
    clearError();

    var phoneDigits = (document.getElementById('guest-phone').value || '').replace(/\D/g, '');
    if (phoneDigits.length !== 10) {
      showError('Enter a valid 10-digit mobile number to place your order.');
      document.getElementById('guest-phone').focus();
      return;
    }

    var btn = document.getElementById('place-btn');
    btn.disabled = true;
    btn.textContent = 'Sending…';
    // Blocks every tap on the menu/cart behind it (fixed, full-viewport, sits above
    // everything) so items can't be added/removed while the order is in flight —
    // otherwise a second tap mid-request could submit a cart that no longer matches
    // what's on screen.
    document.getElementById('processing-overlay').classList.add('show');

    fetchJson('/api/orders/public/' + encodeURIComponent(token), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        guestName: document.getElementById('guest-name').value || null,
        guestPhone: phoneDigits,
        items: lines.map(function (l) { return { menuItemId: l.item.id, qty: l.qty }; }),
      }),
    }).then(function (order) {
      document.getElementById('processing-overlay').classList.remove('show');
      showConfirmation(order);
    }).catch(function (err) {
      document.getElementById('processing-overlay').classList.remove('show');
      showError(err.message);
      btn.disabled = false;
      btn.textContent = 'Place Order';
    });
  }

  function showConfirmation(order) {
    document.getElementById('app').style.display = 'none';
    document.getElementById('cart-bar').style.display = 'none';
    var root = document.getElementById('confirm-root');
    root.innerHTML = '';
    var wrap = el('div', 'wrap');
    var card = el('div', 'confirm-card');
    card.appendChild(el('div', 'badge', '✓'));
    card.appendChild(el('h2', null, 'Order sent to the kitchen!'));
    card.appendChild(el('div', 'order-no', 'Order ' + order.number + ' · ' + order.title));
    card.appendChild(el('div', 'order-total', money(order.total)));
    card.appendChild(el('p', null, "We'll bring it right out. Thank you!"));

    var waText = 'Order ' + order.number + ' at ' +
      (document.getElementById('business-name').textContent || 'the cafe') +
      ' — Total: ' + money(order.total);
    var wa = el('button', 'whatsapp-btn', '📱 Share via WhatsApp');
    wa.onclick = function () {
      window.open('https://wa.me/?text=' + encodeURIComponent(waText), '_blank');
    };
    card.appendChild(wa);

    var again = el('button', 'again-btn', 'Place another order');
    again.onclick = function () { location.reload(); };
    card.appendChild(again);
    wrap.appendChild(card);
    root.appendChild(wrap);
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
      if (state.table.occupied) {
        document.getElementById('occupied-banner').classList.add('show');
      }
      if (state.browseOnly) {
        document.getElementById('browse-banner').classList.add('show');
        document.getElementById('guest-card').style.display = 'none';
      }

      document.getElementById('loading').style.display = 'none';
      document.getElementById('app').style.display = 'block';
      document.getElementById('place-btn').onclick = placeOrder;
      renderBestSellers();
      renderMenu();
      renderCartBar();
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

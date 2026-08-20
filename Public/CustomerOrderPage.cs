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
    /* Keeps a jumped-to heading clear of the pinned strip instead of underneath it. */
    scroll-margin-top: 52px;
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
  /* Two dishes to a row, photo on top. Food is chosen by eye and the old 56px thumbnail was
     too small to choose anything by. Cards stretch to the tallest in their row and the footer
     is pushed down, so prices stay on one line across the grid however long the names run. */
  .item-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; align-items: stretch; }
  .item-card {
    background: var(--card);
    border-radius: 14px;
    overflow: hidden;
    display: flex;
    flex-direction: column;
  }
  /* aspect-ratio rather than a fixed height: the tile's width depends on the phone, and a
     hardcoded height would letterbox the photo on a wide screen and crop it on a narrow one. */
  .item-thumb {
    width: 100%; aspect-ratio: 4 / 3; height: auto; flex: 0 0 auto;
    object-fit: cover; background: var(--input-tint);
  }
  .item-thumb.placeholder {
    display: flex; align-items: center; justify-content: center;
    font-size: 26px;
  }
  .item-info { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 9px 10px 0; }
  .item-name-row { display: flex; align-items: center; gap: 5px; }
  .item-name { font-weight: 700; font-size: 13px; line-height: 1.25; }
  /* Two lines then ellipsis. One is enough for most dishes; a Combo needs the second to say
     what is actually inside it, which is the only place that information exists. */
  .item-sub {
    color: var(--muted); font-size: 11px; line-height: 1.3; margin-top: 3px;
    display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden;
  }
  /* Same treatment as .item-sub, one line taller — this is the only place a no-options item's
     description (e.g. what's included in a combo) ever reaches the customer, since it never
     opens the Customize modal that shows the full text for items with variants/modifiers. */
  .item-desc {
    color: var(--muted); font-size: 11px; line-height: 1.3; margin-top: 3px;
    display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden;
  }
  .item-price { color: var(--heading); font-weight: 700; font-size: 13px; }
  /* Wraps rather than overflows: "from ₹240" beside a Customize button doesn't fit a half-width
     tile on a small phone, and a button pushed off the card edge is unreachable. */
  .item-foot {
    margin-top: auto; padding: 8px 10px 10px;
    display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 6px 8px;
  }
  .item-foot .item-actions-slot { margin-left: auto; }
  .item-grid .stepper { gap: 6px; }
  .item-grid .stepper button { width: 26px; height: 26px; border-radius: 13px; font-size: 16px; }
  .item-grid .stepper button.add { width: auto; padding: 6px 12px; font-size: 11px; }
  .item-grid .customize-btn { padding: 6px 11px; font-size: 11px; }
  .item-grid .item-lines { font-size: 11px; }

  /* Pinned category strip. Without it the only way to reach the drinks is to scroll past
     everything else. Bleeds past .wrap's padding so items scroll under it, not beside it. */
  .cat-strip {
    position: sticky; top: 0; z-index: 20;
    display: flex; gap: 6px; overflow-x: auto;
    margin: 0 -16px 4px; padding: 8px 16px;
    background: var(--bg);
    border-bottom: 1px solid var(--divider);
    scrollbar-width: none;
  }
  .cat-strip::-webkit-scrollbar { display: none; }
  .cat-chip {
    flex: 0 0 auto; white-space: nowrap; cursor: pointer;
    font: inherit; font-size: 12px; font-weight: 700;
    padding: 6px 12px; border-radius: 999px;
    background: var(--card); border: 1px solid var(--divider); color: var(--muted);
  }
  .cat-chip.active { background: var(--heading); border-color: var(--heading); color: #fff; }
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
  /* What a plate actually includes. The grid card clamps its subtitle to two lines, so this
     sheet is the only place the full list is readable — which matters most for a per-head
     buffet, where "what do I get for ₹299" is the whole decision the customer is making. */
  .opt-desc { margin: -8px 0 2px; font-size: 12.5px; line-height: 1.45; color: var(--muted); }
  .opt-desc ul { margin: 0; padding-left: 17px; }
  .opt-desc li { margin: 3px 0; }
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
  /* The summary half of the bar is a button now — it opens the item list. Styled as plain
     text so the bar looks unchanged, with the chevron as the only hint that it does anything. */
  .cart-summary {
    flex: 1; min-width: 0; text-align: left;
    background: none; border: none; padding: 0; font: inherit; color: inherit; cursor: pointer;
  }
  .cart-summary .count { font-size: 12px; color: var(--muted); }
  .cart-summary .total { font-size: 18px; font-weight: 800; display: flex; align-items: center; gap: 5px; }
  .cart-summary .chev { font-size: 12px; color: var(--muted); font-weight: 700; }

  .cart-sheet-btn { width: 100%; margin-top: 10px; }
  .cart-line {
    display: flex; align-items: center; gap: 10px;
    padding: 11px 0; border-bottom: 1px solid var(--divider);
  }
  .cart-line:last-child { border-bottom: none; }
  .cart-line .cl-info { flex: 1; min-width: 0; }
  .cart-line .cl-name { font-weight: 700; font-size: 14px; }
  .cart-line .cl-desc { color: var(--muted); font-size: 12px; margin-top: 2px; }
  .cart-line .cl-amount { font-weight: 700; font-size: 13px; font-variant-numeric: tabular-nums; min-width: 62px; text-align: right; }
  .cart-tot { display: flex; justify-content: space-between; font-size: 13px; padding: 5px 0; }
  .cart-tot.grand { font-weight: 800; font-size: 16px; border-top: 1px solid var(--divider); margin-top: 6px; padding-top: 10px; }
  .cart-tot span:last-child { font-variant-numeric: tabular-nums; }
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
  .settled-sub { color: var(--muted); font-size: 14px; margin: 0 0 14px; }
  /* An <a>, not a <button>, so the PDF opens in its own tab and the browser's own viewer
     handles saving it — a fetch-and-blob download is what phone browsers block silently. */
  .pdf-btn { display: block; margin-top: 18px; text-align: center; text-decoration: none; }
  .past-card { margin-top: 14px; }
  /* The shared input rule above is written for the flex rows it was built for (flex:1), which
     collapses these to nothing in a plain block card — hence the explicit width. */
  .past-card input { display: block; width: 100%; box-sizing: border-box; margin-bottom: 10px; flex: none; }
  .past-card .place-btn { width: 100%; }
  .past-row { display: flex; justify-content: space-between; align-items: center; gap: 10px; padding: 10px 0; border-bottom: 1px solid var(--divider); text-align: left; }
  .past-row:last-child { border-bottom: none; }
  .past-when { font-size: 13px; color: var(--muted); }
  .past-amount { font-weight: 700; }
  .past-send { background: none; border: 1px solid var(--divider); color: var(--heading); border-radius: 10px; padding: 7px 10px; font-size: 12px; font-weight: 600; cursor: pointer; white-space: nowrap; }
  .past-note { font-size: 13px; color: var(--muted); padding-top: 10px; text-align: left; }
  #app, #placed-screen, #bill-screen, #join-screen, #staff-assist-screen, #ended-screen, #waiting-screen, #settled-screen { display: none; }
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
      <div class="field-label" id="guest-name-label">Your name (optional)</div>
      <div class="guest-row">
        <input type="text" id="guest-name" placeholder="e.g. Priya" maxlength="60" />
      </div>
      <div class="field-label" style="margin-top:12px" id="guest-phone-label">Mobile number (optional)</div>
      <div class="guest-row">
        <input type="tel" id="guest-phone" placeholder="10-digit mobile number" maxlength="10" inputmode="numeric" />
      </div>

      <!-- Home delivery only (delivery QR). Hidden and inert for every table/menu QR — the
           dine-in flow never sees these fields. -->
      <div id="delivery-fields" style="display:none">
        <div class="field-label" style="margin-top:12px">Delivery address</div>
        <div class="guest-row">
          <textarea id="guest-address" rows="3" maxlength="300"
            placeholder="House/flat, street, area, landmark"
            style="width:100%;box-sizing:border-box;font:inherit;padding:10px;border-radius:10px;border:1px solid rgba(0,0,0,.15);resize:vertical"></textarea>
        </div>
        <button type="button" id="locate-btn"
          style="margin-top:10px;width:100%;font:inherit;padding:10px;border-radius:10px;border:1px solid rgba(0,0,0,.15);background:#fff;cursor:pointer">
          📍 Use my current location
        </button>
        <div id="locate-status" class="field-label" style="margin-top:8px"></div>
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
    <div id="cat-strip" class="cat-strip" style="display:none"></div>
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
        <!-- Request Bill is hidden for now (product decision), not deleted: the whole flow
             behind it still works end to end — /session/request-bill, the LOCKED status, and
             the bill screen — so re-enabling it later is just dropping this style back off.
             Guests settle at the counter meanwhile. -->
        <button class="place-btn" id="request-bill-btn" style="display:none">Request Bill</button>
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

  <!-- Shown when the session ended because the bill was SETTLED — the one ending that isn't a
       failure. Everything else still falls through to #ended-screen's plain message. -->
  <div id="settled-screen" class="wrap">
    <header><h1 id="settled-business-name">CafePOS</h1><div class="table-line" id="settled-table-line"></div></header>
    <div class="confirm-card">
      <div class="badge">✓</div>
      <h2>Thanks for visiting!</h2>
      <p class="settled-sub">Your bill is settled. Here's what you had.</p>
      <div id="settled-lines" style="text-align:left"></div>
      <a class="place-btn pdf-btn" id="settled-pdf-btn" target="_blank" rel="noopener">Download Bill (PDF)</a>
    </div>

    <!-- Past visits. Only the date and the amount are ever shown here — the bill itself is
         sent to the number, which is what keeps a typed-in number from reading someone
         else's history (see PublicController.MyBills). -->
    <div class="confirm-card past-card">
      <h2>Your previous bills</h2>
      <p class="settled-sub">Enter the name and number you order with.</p>
      <input type="text" id="past-name" placeholder="Name" autocomplete="name" />
      <input type="tel" id="past-phone" placeholder="10-digit mobile number" maxlength="10" inputmode="numeric" autocomplete="tel" />
      <button class="place-btn" id="past-lookup-btn">Show my bills</button>
      <div id="past-result"></div>
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

  <!-- The cart bar counts what's been added but can't say WHAT — a guest four taps in has no
       way to check whether they picked the right variant, or added something twice. This is
       that list, reachable by tapping the bar's own summary. -->
  <div id="cart-overlay" class="opt-overlay">
    <div class="opt-sheet">
      <h3 id="cart-sheet-title">Your order</h3>
      <div id="cart-sheet-lines"></div>
      <div id="cart-sheet-totals"></div>
      <button class="place-btn cart-sheet-btn" id="cart-sheet-place">Place Order</button>
      <button class="secondary-btn cart-sheet-btn" id="cart-sheet-close">Add more items</button>
    </div>
  </div>

  <div id="cart-bar" class="cart-bar" style="display:none">
    <button type="button" class="cart-summary" id="cart-summary-btn">
      <div class="count" id="cart-count">0 items</div>
      <div class="total"><span id="cart-total">₹0.00</span><span class="chev" id="cart-chev">▲</span></div>
    </button>
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
    // True while the guest is deliberately on the menu building a follow-up round after an
    // earlier round already fired. Without it, handleStateUpdate's "already fired -> show the
    // placed screen" rule ran on every 5s poll tick and yanked the guest off the menu the
    // moment they added anything: the item landed in the cart unfired (correct), the next tick
    // bounced them to "Order sent to the kitchen!", and because that screen listed unfired
    // lines too it looked like each tap had fired its own KOT. Nothing had fired at all — the
    // items sat in the cart and the kitchen never saw them. Cleared once the round is actually
    // placed, or when the guest leaves the menu.
    addingMore: false,
    // Home-delivery QR (see QrTokenService.DeliveryTableCode). There is no table, so there is
    // no guest session either: the cart lives entirely in this browser and is sent once, as a
    // whole order, by placeDeliveryOrder. Every delivery-only branch in this file is gated on
    // this flag, so a table/menu QR runs exactly the code it always did.
    deliveryMode: false,
    // Coordinates from the browser's own geolocation, once the customer taps "Use my current
    // location". Null means they didn't (or it failed) — the order still goes through, the cafe
    // just can't hand it to a courier automatically.
    deliveryLat: null,
    deliveryLng: null,
  };
  var pollTimer = null;
  // One entry per cart line while its cart/items POST is in flight — see changeLineQty.
  // Guards against a fast double-tap (or any tap fired again before the previous request for
  // the SAME line finished) sending two independent requests and duplicating the line
  // server-side.
  var pendingLineRequests = {};

  function lineKey(menuItemId, variantId, modifierOptionIds) {
    return menuItemId + '|' + (variantId || '') + '|' + (modifierOptionIds || []).slice().sort(function (a, b) { return a - b; }).join(',');
  }

  // Each cart/items response gets a sequence number. If a later response for a different
  // item arrives before an earlier one, we don't want the old response to overwrite it.
  // Instead, only accept responses that are newer than what we already have.
  var lastResponseSequence = 0;
  var responseSequenceCounter = 0;

  // A server response only reflects the taps it had seen when its request was SENT. Any
  // line still in pendingLineRequests is ahead of that snapshot — re-applying those targets
  // on top of the fresh server order keeps the displayed qty/total at the newest tapped
  // value, instead of falling back and "counting up" (200, 300, ... 1000) as each older
  // response lands.
  function reapplyPendingOptimistic() {
    Object.keys(pendingLineRequests).forEach(function (k) {
      var p = pendingLineRequests[k];
      applyLocalQty(p.menuItemId, p.variantId, p.modifierOptionIds, p.target);
    });
  }

  // Debounce the cart bar render so adding 3 items in quick succession updates the
  // total price once (after all responses land) instead of 3 times (flickering/slow).
  var cartBarRenderTimer = null;
  function scheduleCartBarRender() {
    if (cartBarRenderTimer) return; // Already scheduled, don't reschedule
    cartBarRenderTimer = setTimeout(function () {
      cartBarRenderTimer = null;
      renderCartBar();
    }, 50); // Wait 50ms for more responses to come in, then render once
  }

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
          // The body carries more than its title on some errors — a settled session's 410
          // returns the bill token with it (see ValidateGuestSessionAttribute). Dropping the
          // body here is what would leave the guest with "session ended" and no bill.
          err.body = body;
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
    // Polling reads the guest SESSION, which a home-delivery order doesn't have — left to run
    // it would hammer /session/state with a table code matching no row, and the first 410 would
    // throw a "this session has ended" screen over a perfectly good confirmation.
    if (state.deliveryMode) return;
    stopPolling();
    pollTimer = setInterval(function () {
      fetchJson(sessionBase + '/state').then(handleStateUpdate).catch(function (err) {
        if (err.status !== 410) return;
        // A settled bill is the happy ending, and it's the only 410 that carries a token.
        var token = err.body && err.body.receiptToken;
        if (token) { showSettledScreen(token); return; }
        showEnded(err.message || 'This session has ended.');
      });
    }, 5000);
  }

  // Applied both right after an action and on each poll tick — the session's own status
  // (LOCKED from another device requesting the bill, etc.) always wins over whatever
  // screen we were already showing.
  function unfiredFingerprint(order) {
    return JSON.stringify((order && order.items || []).filter(function (i) { return i.fireBatch === 0 && !i.voided; }));
  }

  function handleStateUpdate(s) {
    // A poll tick's /state fetch can resolve mid-tap: it was requested before this line's
    // own optimistic update, so applying it now would stomp that update and force a full
    // renderMenu() rebuild for no reason. The in-flight request's own .then() carries the
    // authoritative post-tap state anyway, so just skip this tick.
    if (Object.keys(pendingLineRequests).length > 0) return;
    var previousFingerprint = unfiredFingerprint(state.order);
    state.order = s.order;
    // A cancelled order normally also closes the session (the poll then gets a 410), but
    // if the session is somehow still alive, don't silently reset to an empty menu.
    if (s.order && s.order.cancelled) { showEnded('The cafe couldn\'t accept your order. Please speak to a staff member.'); return; }
    if (s.status === 'LOCKED') { showBillScreen(s); return; }
    if (s.order && s.order.pendingStaffConfirmation) { showWaitingScreen(); return; }
    // state.addingMore is what keeps a guest mid-round on the menu — see its declaration.
    // Everything above this line still wins over it: a cancelled order, a bill requested from
    // another device, or a round awaiting staff confirmation all outrank "was picking items".
    if (s.order && s.order.currentFireBatch > 0 && !state.addingMore) { showPlacedScreen(s); return; }

    // Most poll ticks bring back an unchanged cart — nothing on this session changed since
    // the last tick. Skip the render entirely rather than tearing down and re-decoding every
    // item's image (some are multi-MB base64) every 5 seconds for no visible change; a real
    // external change (another device, staff editing the order) still gets the full refresh.
    if (unfiredFingerprint(s.order) === previousFingerprint) { syncCartFromOrder(); return; }

    syncCartFromOrder();
    renderMenu();
    renderBestSellers();
    renderCartBar();
  }

  function hideAllScreens() {
    ['app', 'join-screen', 'staff-assist-screen', 'ended-screen', 'waiting-screen', 'placed-screen', 'bill-screen', 'settled-screen'].forEach(function (id) {
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

  /**
   * The end of a normal visit: staff took the money, the session closed, and this is where the
   * guest gets their bill instead of the dead "session ended" they used to land on.
   *
   * Lines come from the last polled state — by the time the 410 arrives the session is gone and
   * there is nothing left to fetch them from. That copy is accurate for what was ordered, but
   * it predates settlement, so it deliberately shows no total: bill-time discounts, coupons,
   * charges and rounding all land after this snapshot was taken (see OrdersController.Pay), and
   * a stale total on screen is worse than none. The PDF behind the button is the real bill,
   * rendered server-side from the settled order.
   */
  function showSettledScreen(receiptToken) {
    stopPolling();
    hideAllScreens();
    document.getElementById('settled-business-name').textContent = document.getElementById('business-name').textContent;
    document.getElementById('settled-table-line').textContent = document.getElementById('table-line').textContent;

    var linesEl = document.getElementById('settled-lines');
    linesEl.innerHTML = '';
    var items = (state.order && state.order.items || []).filter(function (i) { return !i.voided; });
    items.forEach(function (item) {
      var row = el('div', 'bill-line');
      var desc = lineDescriptor(item);
      row.appendChild(el('span', null, item.qty + '× ' + item.name + (desc ? ' (' + desc + ')' : '')));
      row.appendChild(el('span', null, money(item.price * item.qty)));
      linesEl.appendChild(row);
    });

    document.getElementById('settled-pdf-btn').href = '/api/public/receipt/' + encodeURIComponent(receiptToken);

    // Pre-fill from what they already typed while ordering, so the common case is one tap.
    var typedName = (document.getElementById('guest-name') || {}).value;
    var typedPhone = (document.getElementById('guest-phone') || {}).value;
    if (typedName) document.getElementById('past-name').value = typedName;
    if (typedPhone) document.getElementById('past-phone').value = typedPhone;
    document.getElementById('past-result').innerHTML = '';

    document.getElementById('settled-screen').style.display = 'block';
  }

  function pastBillDate(iso) {
    var d = new Date(iso);
    return isNaN(d) ? '' : d.toLocaleDateString([], { day: 'numeric', month: 'short', year: 'numeric' });
  }

  /**
   * Looks up this customer's past visits at THIS cafe. The list intentionally carries only a
   * date and an amount — the bill itself is sent to their WhatsApp instead, so a number typed
   * in by someone who isn't them never turns into a readable bill (see PublicController).
   *
   * An unmatched name/number comes back as an empty list, not an error, and is reported here
   * as "no bills found" — the page must not tell a guesser which half they got wrong.
   */
  function lookupPastBills() {
    var btn = document.getElementById('past-lookup-btn');
    var resultEl = document.getElementById('past-result');
    var name = (document.getElementById('past-name').value || '').trim();
    var phone = (document.getElementById('past-phone').value || '').replace(/\D/g, '');
    if (name.length < 2 || phone.length !== 10) {
      resultEl.innerHTML = '';
      resultEl.appendChild(el('div', 'past-note', 'Enter your name and your 10-digit mobile number.'));
      return;
    }

    btn.disabled = true;
    resultEl.innerHTML = '';
    resultEl.appendChild(el('div', 'past-note', 'Looking…'));
    fetchJson(apiBase + '/my-bills', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: name, phone: phone }),
    }).then(function (bills) {
      resultEl.innerHTML = '';
      if (!bills || bills.length === 0) {
        resultEl.appendChild(el('div', 'past-note', 'No previous bills found for that name and number.'));
        return;
      }
      bills.forEach(function (bill) {
        var row = el('div', 'past-row');
        var left = el('div', null);
        left.appendChild(el('div', 'past-amount', money(bill.total)));
        left.appendChild(el('div', 'past-when', pastBillDate(bill.createdAt) + ' · ' + bill.number));
        var send = el('button', 'past-send', 'Send to WhatsApp');
        send.addEventListener('click', function () { sendPastBill(bill.number, name, phone, send); });
        row.appendChild(left);
        row.appendChild(send);
        resultEl.appendChild(row);
      });
    }).catch(function (err) {
      resultEl.innerHTML = '';
      // 429 is its own message: "no bills found" would be a lie, and the guest would just
      // keep retrying into the same wall.
      resultEl.appendChild(el('div', 'past-note', err.status === 429
        ? 'Too many tries — please wait a few minutes.'
        : 'Could not load your bills just now.'));
    }).then(function () { btn.disabled = false; });
  }

  /**
   * Queues the chosen bill to the number that was just verified against it. The server always
   * answers 202 whether or not anything matched (so this can't be used to probe), which is
   * also why the button can only ever promise it was sent to their WhatsApp, not that it
   * arrived — delivery depends on the cafe's own WhatsApp being connected.
   */
  function sendPastBill(number, name, phone, btn) {
    btn.disabled = true;
    btn.textContent = 'Sending…';
    fetchJson(apiBase + '/my-bills/send', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: name, phone: phone, number: number }),
    }).then(function () {
      btn.textContent = 'Sent ✓';
    }).catch(function () {
      btn.textContent = 'Try again';
      btn.disabled = false;
    });
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
    var allItems = (s.order.items || []).filter(function (i) { return !i.voided; });
    // Split by fireBatch, because this screen says "Order sent to the kitchen!" and that has
    // to be true of everything under it. It used to list every line regardless, so an item
    // still sitting unfired in the cart appeared here with a status pill as though the kitchen
    // had it — the guest stopped waiting for a Place Order that never happened, and the food
    // was never made. Anything unfired now shows separately, as the cart it actually is.
    var sentItems = allItems.filter(function (i) { return i.fireBatch > 0; });
    var pendingItems = allItems.filter(function (i) { return i.fireBatch === 0; });

    sentItems.forEach(function (item) {
      var row = el('div', null);
      row.style.cssText = 'display:flex;justify-content:space-between;align-items:center;padding:4px 0;text-align:left';
      var desc = lineDescriptor(item);
      var label = el('span', null, item.qty + '× ' + item.name + (desc ? ' (' + desc + ')' : ''));
      var badge = el('span', 'status-pill' + (item.status === 'READY' ? ' ready' : item.status === 'SERVED' ? ' served' : ''), item.status);
      row.appendChild(label);
      row.appendChild(badge);
      itemsEl.appendChild(row);
    });

    if (pendingItems.length > 0) {
      var note = el('div', null, 'Not sent yet — tap "Add more items" to review and send');
      note.style.cssText = 'margin-top:10px;padding-top:8px;border-top:1px dashed #d8cfc4;font-size:13px;color:#8a7a68;text-align:left';
      itemsEl.appendChild(note);
      pendingItems.forEach(function (item) {
        var row = el('div', null);
        row.style.cssText = 'display:flex;justify-content:space-between;align-items:center;padding:4px 0;text-align:left;opacity:.75';
        var desc = lineDescriptor(item);
        row.appendChild(el('span', null, item.qty + '× ' + item.name + (desc ? ' (' + desc + ')' : '')));
        row.appendChild(el('span', 'status-pill', 'IN CART'));
        itemsEl.appendChild(row);
      });
    }

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
    // Ordering is closed once LOCKED, but polling has to keep running: settling is what ends
    // the session from here, and that 410 is now what hands over the bill PDF (see
    // showSettledScreen). Stopping here left a guest who had asked for their bill staring at
    // this screen forever — the one screen where they are most obviously waiting for it.
    startPolling();
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

  // Builds just the cart-dependent part of one item's card — the existing-lines box for
  // combo items, and the Add/stepper/Customize control. Kept separate from the image/name/
  // price so a qty change can patch this alone: full renderMenu() would otherwise tear down
  // and re-decode every item's image (some are multi-MB base64, see MenuItem.Image) on every
  // single tap, which is what made adding items feel laggy.
  function fillItemActions(item, linesSlot, actionsSlot) {
    var hasOptions = (item.variants && item.variants.length > 0) || (item.modifiers && item.modifiers.length > 0);
    var qty = state.cart[item.id] || 0;

    linesSlot.innerHTML = '';
    if (!state.browseOnly && hasOptions) {
      var lines = linesForItem(item.id);
      if (lines.length > 0) {
        var linesBox = el('div', 'item-lines');
        lines.forEach(function (line) {
          var row = el('div', 'item-line-row');
          row.appendChild(el('span', 'line-label', line.qty + '× ' + (lineDescriptor(line) || 'Regular')));
          var stepper = el('div', 'stepper');
          var minus = el('button', null, '−');
          minus.onclick = function (e) { e.stopPropagation(); changeLineQty(item.id, line.variantId, lineOptionIds(line), line.qty - 1); };
          var qtyEl = el('span', 'qty', String(line.qty));
          var plus = el('button', null, '+');
          plus.onclick = function (e) { e.stopPropagation(); changeLineQty(item.id, line.variantId, lineOptionIds(line), line.qty + 1); };
          stepper.appendChild(minus);
          stepper.appendChild(qtyEl);
          stepper.appendChild(plus);
          row.appendChild(stepper);
          linesBox.appendChild(row);
        });
        linesSlot.appendChild(linesBox);
      }
    }

    actionsSlot.innerHTML = '';
    // Ordering is a table-only feature — the no-table "general menu" code (see
    // TablesController.GetMenuOnlyQrToken) is for browsing prices/availability
    // only, so it never gets an Add/quantity stepper at all.
    if (state.browseOnly) return;
    if (hasOptions) {
      var customize = el('button', 'customize-btn', 'Customize');
      customize.onclick = function (e) { e.stopPropagation(); openItemOptions(item); };
      actionsSlot.appendChild(customize);
    } else {
      var stepper2 = el('div', 'stepper');
      if (qty > 0) {
        var minus2 = el('button', null, '−');
        minus2.onclick = function (e) { e.stopPropagation(); changeQty(item.id, -1); };
        var qtyEl2 = el('span', 'qty', String(qty));
        var plus2 = el('button', null, '+');
        plus2.onclick = function (e) { e.stopPropagation(); changeQty(item.id, 1); };
        stepper2.appendChild(minus2);
        stepper2.appendChild(qtyEl2);
        stepper2.appendChild(plus2);
      } else {
        var add = el('button', 'add', 'Add');
        add.onclick = function (e) { e.stopPropagation(); changeQty(item.id, 1); };
        stepper2.appendChild(add);
      }
      actionsSlot.appendChild(stepper2);
    }
  }

  // Re-fills just one item's lines/stepper in place, in lieu of renderMenu() — the fast
  // path after every cart change (see changeLineQty) so only this one item's small button/
  // text subtree is touched, never its (or any other item's) image.
  function patchItemCard(menuItemId) {
    var card = document.querySelector('.item-card[data-item-id="' + menuItemId + '"]');
    if (!card) return;
    var item = state.menu.find(function (m) { return m.id === menuItemId; });
    if (!item) return;
    fillItemActions(item, card.querySelector('.item-lines-slot'), card.querySelector('.item-actions-slot'));
  }

  /**
   * What an item costs at its cheapest — its own price, or the lowest of its variants when it
   * has any. The same number the card prints as "from ₹X", so sorting by it matches what the
   * guest is reading rather than some hidden base price.
   */
  function entryPrice(item) {
    var hasVariants = item.variants && item.variants.length > 0;
    return hasVariants
      ? Math.min.apply(null, [item.price].concat(item.variants.map(function (v) { return v.price; })))
      : item.price;
  }

  function renderMenu() {
    var root = document.getElementById('menu-root');
    root.innerHTML = '';
    var menu = visibleMenu();
    var categories = [];
    menu.forEach(function (item) {
      if (categories.indexOf(item.category) === -1) categories.push(item.category);
    });

    renderCategoryStrip(categories);

    categories.forEach(function (cat) {
      var heading = el('div', 'cat-title', cat);
      heading.dataset.cat = cat;
      root.appendChild(heading);

      var grid = el('div', 'item-grid');
      root.appendChild(grid);

      menu.filter(function (m) { return m.category === cat; })
        // Cheapest first within each category. Sorted on a copy — filter() already returns one,
        // but sorting state.menu itself would reorder every other view that reads it.
        // Unavailable items sink to the bottom of their category whatever they cost: a card
        // nobody can order shouldn't lead the section just because it's the cheapest thing in it.
        .sort(function (a, b) {
          if (a.available !== b.available) return a.available ? -1 : 1;
          return entryPrice(a) - entryPrice(b);
        })
        .forEach(function (item) {
        var card = el('div', 'item-card' + (item.available ? '' : ' unavailable'));
        card.dataset.itemId = String(item.id);

        if (item.image) {
          var img = document.createElement('img');
          img.className = 'item-thumb';
          img.src = item.image;
          img.alt = item.name;
          card.appendChild(img);
        } else {
          card.appendChild(el('div', 'item-thumb placeholder', '🍽️'));
        }

        var hasOptions = (item.variants && item.variants.length > 0) || (item.modifiers && item.modifiers.length > 0);
        var info = el('div', 'item-info');
        var nameRow = el('div', 'item-name-row');
        var badge = vnvBadge(item.vegNonVegType);
        if (badge) nameRow.appendChild(badge);
        nameRow.appendChild(el('div', 'item-name', item.name));
        info.appendChild(nameRow);
        if (item.subtitle) info.appendChild(el('div', 'item-sub', item.subtitle));
        // Items with variants/modifiers get their description in the Customize modal
        // (renderItemOptions) instead — showing it here too would just repeat it.
        if (!hasOptions && item.description) info.appendChild(el('div', 'item-desc', item.description));
        if (!item.available) info.appendChild(el('div', 'unavailable-tag', 'CURRENTLY UNAVAILABLE'));

        var linesSlot = el('div', 'item-lines-slot');
        info.appendChild(linesSlot);
        card.appendChild(info);

        // Price and control share the card's bottom edge, so they line up across the grid
        // regardless of how tall the name and subtitle above them ended up.
        var foot = el('div', 'item-foot');
        foot.appendChild(el('div', 'item-price', (hasOptions ? 'from ' : '') + money(entryPrice(item))));
        var actionsSlot = el('div', 'item-actions-slot');
        foot.appendChild(actionsSlot);
        card.appendChild(foot);

        fillItemActions(item, linesSlot, actionsSlot);

        // The whole card is the tap target, not just the small "Add" button in the corner —
        // a thumb on a phone is nowhere near as precise as a mouse on the POS, and hunting for
        // that button is what made ordering feel clumsy.
        //
        // The stepper/Customize/per-line buttons (see fillItemActions) each call
        // e.stopPropagation() in their own handler — that's the ONLY thing that reliably keeps
        // a button tap from also being read as a card tap. An e.target.closest('.item-actions-
        // slot') check here looked like it should do the same job and doesn't: tapping "Add"
        // runs the button's own handler first, which calls changeQty -> patchItemCard, which
        // synchronously replaces actionsSlot's innerHTML (new qty, new stepper) WHILE this
        // click event is still bubbling. That detaches the original button from the DOM before
        // the event reaches here, so e.target (still that now-parentless button) no longer has
        // '.item-actions-slot' as an ancestor and closest() finds nothing — the guard silently
        // fails and this handler adds a second unit on top of the one the button just added.
        // Same failure, opposite direction, is why "−" looked like it did nothing: −1 from the
        // button immediately followed by +1 from this handler net out to zero. stopPropagation
        // stops the event before any of that DOM-mutation timing can matter; the closest()
        // check stays as harmless defense-in-depth, not the actual guard.
        if (!state.browseOnly && item.available) {
          card.style.cursor = 'pointer';
          card.onclick = function (e) {
            if (e.target.closest('.item-actions-slot') || e.target.closest('.item-lines-slot')) return;
            if (hasOptions) { openItemOptions(item); return; }
            changeQty(item.id, 1);
          };
        }

        grid.appendChild(card);
      });
    });

    syncActiveChip();
  }

  /**
   * The pinned category jump strip. Rebuilt with the menu because the Veg Only filter can
   * empty a category entirely, and a chip that scrolls to nothing is worse than no chip.
   */
  function renderCategoryStrip(categories) {
    var strip = document.getElementById('cat-strip');
    strip.innerHTML = '';
    // One category is every category — the strip would just be a label taking up the top of
    // the screen on a short menu.
    strip.style.display = categories.length > 1 ? 'flex' : 'none';
    if (categories.length <= 1) return;

    categories.forEach(function (cat) {
      var chip = el('button', 'cat-chip', cat);
      chip.type = 'button';
      chip.dataset.cat = cat;
      chip.onclick = function () {
        var heading = document.querySelector('.cat-title[data-cat="' + cssEscape(cat) + '"]');
        if (heading) heading.scrollIntoView({ behavior: 'smooth', block: 'start' });
      };
      strip.appendChild(chip);
    });
  }

  /** Category names are cafe-entered free text, so they can contain quotes and anything else
   * a selector would choke on. */
  function cssEscape(value) {
    return window.CSS && CSS.escape ? CSS.escape(value) : String(value).replace(/["\\]/g, '\\$&');
  }

  /**
   * Marks the chip for whichever category is currently under the strip, so the strip keeps
   * telling the truth when the guest scrolls by hand instead of tapping.
   */
  function syncActiveChip() {
    var headings = Array.prototype.slice.call(document.querySelectorAll('.cat-title'));
    if (headings.length === 0) return;
    var line = 60; // just below the pinned strip
    var current = headings[0];
    headings.forEach(function (h) {
      if (h.getBoundingClientRect().top <= line) current = h;
    });
    Array.prototype.slice.call(document.querySelectorAll('.cat-chip')).forEach(function (chip) {
      chip.classList.toggle('active', chip.dataset.cat === current.dataset.cat);
    });
  }

  // Same stepper markup as fillItemActions' plain-item branch, into a caller-supplied slot.
  // A best seller can have variants/modifiers just like any other item, so this mirrors
  // fillItemActions' hasOptions branch too — skipping that check used to let a Best Sellers
  // tap add a variant-less line straight to the cart instead of asking which size/modifier.
  function fillBestSellerStepper(item, actionsSlot) {
    actionsSlot.innerHTML = '';
    if (state.browseOnly) return;
    var hasOptions = (item.variants && item.variants.length > 0) || (item.modifiers && item.modifiers.length > 0);
    if (hasOptions) {
      var customize = el('button', 'customize-btn', 'Customize');
      customize.onclick = function () { openItemOptions(item); };
      actionsSlot.appendChild(customize);
      return;
    }
    var qty = state.cart[item.id] || 0;
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
    actionsSlot.appendChild(stepper);
  }

  function patchBestSellerCard(menuItemId) {
    var card = document.querySelector('.bs-card[data-item-id="' + menuItemId + '"]');
    if (!card) return;
    var item = state.bestSellers.find(function (m) { return m.id === menuItemId; });
    if (!item) return;
    fillBestSellerStepper(item, card.querySelector('.item-actions-slot'));
  }

  function renderBestSellers() {
    var section = document.getElementById('bestsellers-section');
    if (!state.bestSellers.length) { section.style.display = 'none'; return; }
    section.style.display = 'block';
    var strip = document.getElementById('bestsellers-strip');
    strip.innerHTML = '';
    state.bestSellers.forEach(function (item) {
      var card = el('div', 'bs-card' + (item.available ? '' : ' unavailable'));
      card.dataset.itemId = String(item.id);
      var bsHasOptions = (item.variants && item.variants.length > 0) || (item.modifiers && item.modifiers.length > 0);
      card.appendChild(el('div', 'item-name', item.name));
      card.appendChild(el('div', 'item-price', (bsHasOptions ? 'from ' : '') + money(entryPrice(item))));

      var actionsSlot = el('div', 'item-actions-slot');
      card.appendChild(actionsSlot);
      fillBestSellerStepper(item, actionsSlot);

      strip.appendChild(card);
    });
  }

  // Every tap fabricates a full optimistic order-item line, not just a bump of the display-only
  // state.cart map — that's what lets cartCount()/cartSubtotal(), and so the cart bar, move on
  // the tap instead of only the tapped item's own stepper. Fabricated lines are temporary: they
  // are discarded and replaced wholesale by the server's real Order the moment the request
  // resolves (state.order = s.order in sendCartRequest's .then).
  function applyLocalQty(menuItemId, variantId, modifierOptionIds, nextQty) {
    var isPlain = !variantId && (!modifierOptionIds || modifierOptionIds.length === 0);

    // The server builds the Order itself, on the FIRST cart/items POST — session.OrderId is
    // null until then (see GuestSessionController.AddCartItem) — so on a fresh table there is
    // nothing here to hang an optimistic line off. That mattered because cartCount()/
    // cartSubtotal() read ONLY unfiredLines(), i.e. state.order: left null, the very first Add
    // updated the tapped card's own stepper instantly but the cart bar at the bottom of the
    // screen stayed HIDDEN for the whole round trip — and that first request is the slowest of
    // the session (it creates the order, and the customer, under the session lock). A local
    // shell gives that first line somewhere to live so the bar appears on the tap. It is
    // temporary: the server's real Order replaces it wholesale when the response lands, and
    // the failure path in sendCartRequest restores this null.
    if (!state.order) state.order = { items: [] };
    var items = state.order.items || (state.order.items = []);

    if (isPlain) {
      state.cart[menuItemId] = nextQty;
      var plainIdx = items.findIndex(function (i) {
        return i.menuItemId === menuItemId && !i.variantId && lineOptionIds(i).length === 0 && i.fireBatch === 0 && !i.voided;
      });
      if (nextQty <= 0) {
        if (plainIdx !== -1) items.splice(plainIdx, 1);
      } else if (plainIdx !== -1) {
        items[plainIdx].qty = nextQty;
      } else {
        items.push(buildOptimisticLine(menuItemId, null, [], nextQty));
      }
      return true;
    }

    // Existing combo line (already has a real price/id from a prior round trip) — safe to
    // mutate its qty in place.
    var sortedIds = (modifierOptionIds || []).slice().sort(function (a, b) { return a - b; });
    var idx = items.findIndex(function (i) {
      if (i.menuItemId !== menuItemId || i.fireBatch !== 0 || i.voided) return false;
      var lineIds = lineOptionIds(i).sort(function (a, b) { return a - b; });
      return i.variantId === variantId && lineIds.length === sortedIds.length && lineIds.every(function (id, k) { return id === sortedIds[k]; });
    });
    if (idx !== -1) {
      if (nextQty <= 0) items.splice(idx, 1); else items[idx].qty = nextQty;
      return true;
    }
    if (nextQty <= 0) return true; // nothing local to remove; the server has nothing either

    // Brand-new combo line. This used to bail out (return false) and wait for the server to
    // supply the line's price — which meant the options sheet closed onto a menu where
    // nothing had visibly changed until the response landed. The price isn't actually unknown:
    // it's the same arithmetic the sheet's own Add button just quoted, so build the line here
    // and let the server's response replace it.
    items.push(buildOptimisticLine(menuItemId, variantId, sortedIds, nextQty));
    return true;
  }

  /**
   * One optimistic cart line, priced client-side the way the options sheet already quoted it
   * (variant price, or the base price, plus every selected add-on). The shape deliberately
   * mirrors a server OrderItem — fireBatch 0, voided false — because unfiredLines() and so
   * cartCount/cartSubtotal/fillItemActions read it without caring who built it.
   *
   * Display only, in both callers: dine-in throws it away the moment the cart/items response
   * brings back the server's real Order, and a delivery order is re-priced server-side from
   * menuItemId/variantId/option ids when it's placed. A stale menu here can't underpay.
   */
  function buildOptimisticLine(menuItemId, variantId, sortedIds, nextQty) {
    var menuItem = state.menu.find(function (m) { return m.id === menuItemId; }) || {};
    var variant = (menuItem.variants || []).find(function (v) { return v.id === variantId; });

    // Every option this item offers, flattened across its groups — same walk the options modal
    // does (optSelectedOptions), and the field is `modifiers`, not `modifierGroups`.
    var catalogue = [];
    (menuItem.modifiers || []).forEach(function (g) { catalogue = catalogue.concat(g.options || []); });

    // sortedIds may hold the same option id more than once — that's how "extra cheese ×2" is
    // expressed (see optOptionQty). Each occurrence is priced, and repeats are folded back into
    // one {modifierOptionId, qty} entry so lineOptionIds() re-expands to exactly this list.
    var byOption = [];
    var optionsTotal = 0;
    (sortedIds || []).forEach(function (id) {
      var option = catalogue.find(function (o) { return o.id === id; });
      if (!option) return;
      optionsTotal += option.price || 0;
      var existing = byOption.find(function (e) { return e.modifierOptionId === id; });
      if (existing) existing.qty += 1;
      else byOption.push({ modifierOptionId: id, name: option.name, price: option.price, qty: 1 });
    });

    return {
      menuItemId: menuItemId,
      // Carried on the line because a delivery confirmation screen lists items by name and,
      // with no server Order to read back there, this is the only place that name survives.
      name: menuItem.name,
      qty: nextQty,
      price: (variant ? variant.price : (menuItem.price || 0)) + optionsTotal,
      fireBatch: 0,
      voided: false,
      status: 'NEW',
      variantId: variantId || null,
      variantName: variant ? variant.name : null,
      selectedModifiers: byOption,
    };
  }

  /**
   * The delivery cart's only writer. Unlike applyLocalQty, whose optimistic lines are replaced by
   * the server's real Order a moment later, these lines ARE the record — nothing is coming to
   * correct them, since the whole order goes up in one request when the customer places it.
   * Both build their lines with buildOptimisticLine, so what the options sheet quoted and what
   * lands in either cart cannot drift apart.
   */
  function applyDeliveryQty(menuItemId, variantId, modifierOptionIds, nextQty) {
    if (!state.order) state.order = { items: [] };
    var items = state.order.items || (state.order.items = []);
    var sortedIds = (modifierOptionIds || []).slice().sort(function (a, b) { return a - b; });
    var isPlain = !variantId && sortedIds.length === 0;
    if (isPlain) state.cart[menuItemId] = nextQty;

    var idx = items.findIndex(function (i) {
      if (i.menuItemId !== menuItemId) return false;
      var lineIds = lineOptionIds(i).sort(function (a, b) { return a - b; });
      return (i.variantId || null) === (variantId || null)
        && lineIds.length === sortedIds.length
        && lineIds.every(function (id, k) { return id === sortedIds[k]; });
    });

    if (nextQty <= 0) {
      if (idx !== -1) items.splice(idx, 1);
      return;
    }
    if (idx !== -1) { items[idx].qty = nextQty; return; }

    items.push(buildOptimisticLine(menuItemId, variantId, sortedIds, nextQty));
  }

  // Every tap updates the tapped item's own card instantly via applyLocalQty/patchItemCard
  // (no waiting on the network) — the request below is fired in the background and only
  // reconciles state afterward, which is normally a no-op since the optimistic guess already
  // matches. variantId/modifierOptionIds (not just menuItemId) identify WHICH line to
  // upsert server-side (see OrderBuildingService.AddOrUpdateCartItemAsync), so an existing
  // line's qty change must resend the exact same combo. If the request fails, the snapshots
  // below put both possible optimistic targets (state.cart for a plain item, state.order for
  // a combo line) back exactly how they were, then patch the one card that changed.
  function changeLineQty(menuItemId, variantId, modifierOptionIds, nextQty) {
    clearError();
    var next = Math.max(0, nextQty);
    var key = lineKey(menuItemId, variantId, modifierOptionIds);
    var pending = pendingLineRequests[key];

    if (pending) {
      // A cart/items POST for this exact line is already in flight — firing another one
      // here is what let a fast double-tap race two requests past each other and insert
      // the item twice server-side. Apply this tap's effect locally for instant feedback
      // and let the in-flight request notice the newer target once it resolves.
      pending.target = next;
      var patched = applyLocalQty(menuItemId, variantId, modifierOptionIds, next);
      if (patched) { patchItemCard(menuItemId); patchBestSellerCard(menuItemId); renderCartBar(); }
      return;
    }

    // Home delivery has no session to POST a cart line to — the whole order goes up in one
    // request when the customer places it. So the local edit IS the cart here, and there is
    // nothing to reconcile against afterwards.
    if (state.deliveryMode) {
      applyDeliveryQty(menuItemId, variantId, modifierOptionIds, next);
      patchItemCard(menuItemId);
      patchBestSellerCard(menuItemId);
      renderCartBar();
      return;
    }

    var previousCartQty = state.cart[menuItemId];
    var previousOrderSnapshot = state.order ? JSON.parse(JSON.stringify(state.order)) : null;
    var patchedOptimistically = applyLocalQty(menuItemId, variantId, modifierOptionIds, next);
    if (patchedOptimistically) {
      patchItemCard(menuItemId);
      patchBestSellerCard(menuItemId);
      renderCartBar();
    }

    var requestSeq = ++responseSequenceCounter;
    pendingLineRequests[key] = { target: next, menuItemId: menuItemId, variantId: variantId, modifierOptionIds: modifierOptionIds };
    sendCartRequest(menuItemId, variantId, modifierOptionIds, key, next, requestSeq, previousCartQty, previousOrderSnapshot, patchedOptimistically);
  }

  function sendCartRequest(menuItemId, variantId, modifierOptionIds, key, sentQty, requestSeq, previousCartQty, previousOrderSnapshot, patchedOptimistically) {
    var phoneDigits = (document.getElementById('guest-phone').value || '').replace(/\D/g, '');

    fetchJson(sessionBase + '/cart/items', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        menuItemId: menuItemId,
        qty: sentQty,
        modifier: null,
        variantId: variantId || null,
        modifierOptionIds: (modifierOptionIds && modifierOptionIds.length) ? modifierOptionIds : null,
        guestName: document.getElementById('guest-name').value || null,
        guestPhone: phoneDigits || null,
      }),
    }).then(function (s) {
      var stillPending = pendingLineRequests[key];
      if (stillPending && stillPending.target !== sentQty) {
        // A newer tap changed the target for this line while the request was in flight —
        // send one more request (with a fresh sequence number, so its response counts as
        // the newest) instead of leaving the cart out of sync.
        sendCartRequest(menuItemId, variantId, modifierOptionIds, key, stillPending.target, ++responseSequenceCounter, previousCartQty, previousOrderSnapshot, patchedOptimistically);
      } else {
        delete pendingLineRequests[key];
      }

      // Only accept this response if it's newer than what we've already processed —
      // a slow response for an early tap must not overwrite a fast response for a
      // later tap. Stale responses render nothing at all.
      if (requestSeq < lastResponseSequence) return;
      lastResponseSequence = requestSeq;
      state.order = s.order;
      // This snapshot only reflects the taps the server had seen when THIS request was
      // sent — any line still pending (including this one, if it's being re-sent above)
      // is ahead of it. Re-apply those targets on top so the displayed total never falls
      // backwards and then "counts up" as older responses trickle in.
      reapplyPendingOptimistic();
      syncCartFromOrder();
      if (state.order) document.getElementById('guest-card').style.display = 'none';
      patchItemCard(menuItemId);
      patchBestSellerCard(menuItemId);
      scheduleCartBarRender(); // Debounced: batches updates when multiple items added quickly
    }).catch(function (err) {
      delete pendingLineRequests[key];
      if (patchedOptimistically) {
        state.order = previousOrderSnapshot;
        if (previousCartQty === undefined) delete state.cart[menuItemId]; else state.cart[menuItemId] = previousCartQty;
        patchItemCard(menuItemId);
        patchBestSellerCard(menuItemId);
        scheduleCartBarRender();
      }
      if (err.status === 410) { showEnded(err.message || 'This session has ended.'); return; }
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

    // The inclusion list, e.g. "1 type of soup • 8 hot starters • Dessert (single serve)".
    // Bullet-separated text becomes a real list so each line is scannable; anything else
    // renders as one paragraph. textContent rather than el()'s innerHTML because this is
    // free text the cafe typed, and it has no business carrying markup into the page.
    if (item.description) {
      var desc = el('div', 'opt-desc');
      var parts = item.description.split('•')
        .map(function (s) { return s.trim(); })
        .filter(function (s) { return s.length > 0; });
      if (parts.length > 1) {
        var ul = document.createElement('ul');
        parts.forEach(function (p) {
          var li = document.createElement('li');
          li.textContent = p;
          ul.appendChild(li);
        });
        desc.appendChild(ul);
      } else {
        desc.textContent = item.description;
      }
      body.appendChild(desc);
    }

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

  /**
   * The cart's contents, which the bar alone could never show. Built from unfiredLines() — the
   * same list Place Order actually sends — rather than from state.cart, because that flat
   * id→qty map cannot represent WHICH variant or add-ons were picked, and that is exactly what
   * a guest opens this to check.
   *
   * Each row edits in place through changeLineQty, the same call the per-item steppers use, so
   * removing a wrongly-picked line never means hunting back through the menu for it.
   */
  function renderCartSheet() {
    var linesEl = document.getElementById('cart-sheet-lines');
    var totalsEl = document.getElementById('cart-sheet-totals');
    var lines = unfiredLines();
    linesEl.innerHTML = '';
    totalsEl.innerHTML = '';

    document.getElementById('cart-sheet-title').textContent =
      lines.length === 0 ? 'Your order' : 'Your order · ' + cartCount() + (cartCount() === 1 ? ' item' : ' items');

    if (lines.length === 0) {
      linesEl.appendChild(el('div', 'past-note', 'Nothing added yet.'));
      document.getElementById('cart-sheet-place').style.display = 'none';
      return;
    }
    document.getElementById('cart-sheet-place').style.display = '';

    lines.forEach(function (line) {
      var row = el('div', 'cart-line');
      var info = el('div', 'cl-info');
      info.appendChild(el('div', 'cl-name', line.name));
      var desc = lineDescriptor(line);
      if (desc) info.appendChild(el('div', 'cl-desc', desc));
      row.appendChild(info);

      var stepper = el('div', 'stepper');
      var minus = el('button', null, '−');
      minus.onclick = function () { changeLineQty(line.menuItemId, line.variantId, lineOptionIds(line), line.qty - 1); };
      var plus = el('button', null, '+');
      plus.onclick = function () { changeLineQty(line.menuItemId, line.variantId, lineOptionIds(line), line.qty + 1); };
      stepper.appendChild(minus);
      stepper.appendChild(el('span', 'qty', String(line.qty)));
      stepper.appendChild(plus);
      row.appendChild(stepper);

      row.appendChild(el('div', 'cl-amount', money(line.price * line.qty)));
      linesEl.appendChild(row);
    });

    var subtotal = cartSubtotal();
    var tax = subtotal * (state.taxRatePct / 100);
    var add = function (cls, label, value) {
      var r = el('div', cls);
      r.appendChild(el('span', null, label));
      r.appendChild(el('span', null, money(value)));
      totalsEl.appendChild(r);
    };
    add('cart-tot', 'Subtotal', subtotal);
    add('cart-tot', 'Tax', tax);
    add('cart-tot grand', 'Total', subtotal + tax);
  }

  function cartSheetOpen() {
    return document.getElementById('cart-overlay').classList.contains('show');
  }

  function openCartSheet() {
    renderCartSheet();
    document.getElementById('cart-overlay').classList.add('show');
    document.getElementById('cart-chev').textContent = '▼';
  }

  function closeCartSheet() {
    document.getElementById('cart-overlay').classList.remove('show');
    document.getElementById('cart-chev').textContent = '▲';
  }

  function renderCartBar() {
    var bar = document.getElementById('cart-bar');
    var placeBtn = document.getElementById('place-btn');
    var count = cartCount();

    if (count === 0) {
      // Emptying the cart from inside the sheet leaves nothing for it to list, so it closes
      // itself rather than sitting there over a menu the guest now needs to get back to.
      if (cartSheetOpen()) closeCartSheet();
      // Mid-round with nothing picked yet: the poll no longer drags the guest back to the
      // placed screen (that's the whole point of state.addingMore), so without this there'd be
      // no way off the menu for someone who changed their mind. Same bar, different job.
      if (state.addingMore) {
        bar.style.display = 'flex';
        document.getElementById('cart-count').textContent = 'Nothing added yet';
        document.getElementById('cart-total').textContent = '';
        placeBtn.textContent = 'Back to your order';
        placeBtn.disabled = false;
        placeBtn.onclick = function () {
          state.addingMore = false;
          if (state.order) showPlacedScreen({ order: state.order });
        };
        return;
      }
      bar.style.display = 'none';
      return;
    }

    bar.style.display = 'flex';
    var subtotal = cartSubtotal();
    var tax = subtotal * (state.taxRatePct / 100);
    document.getElementById('cart-count').textContent = count + (count === 1 ? ' item' : ' items') + ' · incl. tax';
    document.getElementById('cart-total').textContent = money(subtotal + tax);
    // Reclaim the button from the empty-cart branch above — it may still be wired to "Back to
    // your order" from a moment ago, and the label has to say what this round actually does.
    placeBtn.textContent = state.addingMore ? 'Send to Kitchen' : 'Place Order';
    placeBtn.disabled = false;
    placeBtn.onclick = placeOrder;
    // Every cart change lands here, so this is the one place that keeps an open sheet honest
    // — including changes made from the sheet's own steppers.
    if (cartSheetOpen()) renderCartSheet();
    document.getElementById('cart-sheet-place').textContent = placeBtn.textContent;
  }

  /**
   * Asks the browser where the phone is. This is the whole reason a courier can be dispatched
   * automatically: a typed Indian address ("gali no. 5, behind the temple") is something a rider
   * can work with but a routing API cannot, and the device already knows the answer to within a
   * few metres. Failure is never fatal — the address alone still places the order.
   */
  function requestLocation() {
    var status = document.getElementById('locate-status');
    var btn = document.getElementById('locate-btn');
    if (!navigator.geolocation) {
      status.textContent = 'This browser can’t share a location — your address alone is fine.';
      return;
    }
    btn.disabled = true;
    status.textContent = 'Finding your location…';
    navigator.geolocation.getCurrentPosition(
      function (position) {
        btn.disabled = false;
        state.deliveryLat = position.coords.latitude;
        state.deliveryLng = position.coords.longitude;
        status.textContent = '✅ Location shared — the rider will find you faster.';
      },
      function () {
        btn.disabled = false;
        state.deliveryLat = null;
        state.deliveryLng = null;
        // Deliberately not an error banner: declining is a legitimate choice, and the order is
        // still perfectly placeable. Saying so stops people abandoning the cart here.
        status.textContent = 'Couldn’t get your location. You can still order — just make the address as clear as you can.';
      },
      // A stale fix from another part of the city is worse than none, hence maximumAge 0.
      { enableHighAccuracy: true, timeout: 10000, maximumAge: 0 }
    );
  }

  /**
   * Sends a home-delivery order as one request. No session, no fire batches, no partial state:
   * either the whole order is accepted or nothing happened. The cafe still has to accept it and
   * press Book rider — placing this books no courier and spends nothing.
   */
  function placeDeliveryOrder() {
    if (cartCount() === 0) return;
    clearError();

    var name = (document.getElementById('guest-name').value || '').trim();
    var phone = (document.getElementById('guest-phone').value || '').replace(/\D/g, '');
    var address = (document.getElementById('guest-address').value || '').trim();
    // Checked here as well as server-side so the customer is told before the round trip, not by
    // a 400 after it. PublicController.CreateDeliveryOrder remains the authority.
    if (!name) { showError('Please enter your name.'); return; }
    if (phone.length !== 10) { showError('Enter a 10-digit mobile number so the rider can call you.'); return; }
    if (!address) { showError('Please enter your delivery address.'); return; }

    var items = unfiredLines().map(function (line) {
      return {
        menuItemId: line.menuItemId,
        qty: line.qty,
        modifier: null,
        variantId: line.variantId || null,
        modifierOptionIds: lineOptionIds(line).length ? lineOptionIds(line) : null,
      };
    });

    var btn = document.getElementById('place-btn');
    btn.disabled = true;
    btn.textContent = 'Sending…';
    document.getElementById('processing-overlay').classList.add('show');

    fetchJson(apiBase + '/delivery-order', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        guestName: name,
        guestPhone: phone,
        address: address,
        latitude: state.deliveryLat,
        longitude: state.deliveryLng,
        items: items,
      }),
    }).then(function (placed) {
      document.getElementById('processing-overlay').classList.remove('show');
      // The confirmation screen lists what was sent, and the server's reply carries only the id
      // and total — so the lines come from the cart that was just accepted, marked as fired
      // (fireBatch > 0) since that is exactly what they now are. Total comes from the server,
      // which priced it, not from the local display arithmetic.
      var sent = JSON.parse(JSON.stringify(state.order.items || []));
      sent.forEach(function (line) { line.fireBatch = 1; line.status = 'NEW'; });

      // Cleared before the screen switches: this is what stops a second tap re-sending food
      // that the kitchen already has.
      state.order = { items: [] };
      state.cart = {};

      // Staff-Confirm Mode is on for this cafe, so the kitchen hasn't started yet — saying
      // "sent to the kitchen" here would be a lie the customer plans their evening around.
      if (placed.pendingConfirmation) { showDeliveryWaitingScreen(placed.orderToken); return; }
      showPlacedScreen({ order: { items: sent, total: placed.total } });
    }).catch(function (err) {
      document.getElementById('processing-overlay').classList.remove('show');
      btn.disabled = false;
      btn.textContent = 'Place Order';
      showError(err.message);
    });
  }

  /**
   * The delivery twin of the dine-in waiting screen. It deliberately does NOT poll: polling
   * there reads the guest session, which a delivery order has none of, and inventing a public
   * "is my order approved yet" endpoint would let anyone holding the QR walk order ids. The
   * cafe has the customer's number and calls if there's a problem, so the copy says that
   * plainly rather than leaving someone watching a spinner for an answer that isn't coming.
   */
  var deliveryStatusTimer = null;

  function stopDeliveryStatusPolling() {
    if (deliveryStatusTimer) { clearInterval(deliveryStatusTimer); deliveryStatusTimer = null; }
  }

  function showDeliveryWaitingScreen(orderToken) {
    hideAllScreens();
    var screen = document.getElementById('waiting-screen');
    screen.querySelector('h2').textContent = 'Order sent — waiting for the cafe to confirm';
    screen.querySelector('p').textContent =
      'The cafe will confirm your order shortly and start cooking. They have your number and will call if anything is unclear.';
    screen.style.display = 'flex';
    startDeliveryStatusPolling(orderToken);
  }

  /**
   * The delivery twin of the dine-in session poll, reading a status endpoint scoped to this one
   * order's own signed token rather than the shared guest session this flow never has (see
   * PublicController.DeliveryOrderStatus). Runs only while a real reason exists to keep asking —
   * pending confirmation — and stops itself the moment that stops being true, one way or the
   * other, so it can never poll forever nor overlap a previous call still in flight.
   */
  function startDeliveryStatusPolling(orderToken) {
    stopDeliveryStatusPolling();
    var checking = false;
    deliveryStatusTimer = setInterval(function () {
      if (checking) return; // a slow response must not let two checks race each other
      checking = true;
      fetchJson('/api/public/delivery-order-status/' + encodeURIComponent(orderToken))
        .then(function (s) {
          checking = false;
          if (s.cancelled) {
            stopDeliveryStatusPolling();
            showEnded(s.cancelReason
              ? ('The cafe declined this order: ' + s.cancelReason)
              : 'The cafe declined this order. Please call them if you\'d like to know why.');
            return;
          }
          if (!s.pendingStaffConfirmation) {
            // Confirmed — the kitchen has it now. There is no live item/status list to show
            // (this order was never tracked client-side the way a table session is), so this
            // simply says so rather than presenting a placed-screen it can't honestly fill in.
            stopDeliveryStatusPolling();
            hideAllScreens();
            document.getElementById('waiting-screen').querySelector('h2').textContent = 'Confirmed — your order is being prepared';
            document.getElementById('waiting-screen').querySelector('p').textContent =
              'The cafe has accepted your order. Sit tight — they’ll get it on its way to you.';
            document.getElementById('waiting-screen').style.display = 'flex';
          }
        })
        .catch(function () { checking = false; }); // transient network blip — the next tick retries
    }, 5000);
  }

  // Held while "Place Order" is waiting out an in-flight cart/items request — see placeOrder.
  var placeWaitTimer = null;

  /**
   * The cart bar now appears on the tap that fills it, which can be before the first
   * cart/items POST has resolved (see applyLocalQty's shell order) — and until it does, the
   * session has no Order at all server-side, so PlaceOrder would answer "Your cart is empty."
   * Nothing is wrong in that moment except timing, so wait the request out rather than
   * surfacing an error for it. The cap hands the button back if the network never answers.
   */
  function waitForCartThenPlace() {
    var btn = document.getElementById('place-btn');
    btn.disabled = true;
    btn.textContent = 'Sending…';
    document.getElementById('processing-overlay').classList.add('show');

    var waitedMs = 0;
    if (placeWaitTimer) clearInterval(placeWaitTimer);
    placeWaitTimer = setInterval(function () {
      waitedMs += 100;
      var settled = Object.keys(pendingLineRequests).length === 0;
      if (!settled && waitedMs < 10000) return;

      clearInterval(placeWaitTimer);
      placeWaitTimer = null;
      document.getElementById('processing-overlay').classList.remove('show');
      btn.disabled = false;
      btn.textContent = state.addingMore ? 'Send to Kitchen' : 'Place Order';
      // Settled but empty means that request FAILED and rolled its line back — sendCartRequest
      // has already shown why, so just leave the guest on the menu.
      if (settled) { if (cartCount() > 0) placeOrder(); return; }
      showError('Still saving your last item — please try again in a moment.');
    }, 100);
  }

  function placeOrder() {
    if (state.deliveryMode) { placeDeliveryOrder(); return; }
    if (cartCount() === 0) return;
    if (Object.keys(pendingLineRequests).length > 0) { waitForCartThenPlace(); return; }
    clearError();

    var btn = document.getElementById('place-btn');
    btn.disabled = true;
    btn.textContent = 'Sending…';
    document.getElementById('processing-overlay').classList.add('show');

    fetchJson(sessionBase + '/order', { method: 'POST' }).then(function (s) {
      document.getElementById('processing-overlay').classList.remove('show');
      state.order = s.order;
      // Every line that was in state.cart just fired and is no longer an unfired (fireBatch:0)
      // line — but state.cart itself was never told that. Left unsynced, a later trip to
      // "Add more items" reads the SAME stale quantities off it and renders already-fired
      // items as if they still had an editable stepper. Tapping "−" on one of those doesn't
      // touch the fired line at all (applyLocalQty only ever matches an unfired line for that
      // menu item) — finding none, it silently creates a BRAND NEW unfired line instead, so a
      // guest trying to correct "4 Ice Teas" down to 3 actually orders 3 MORE. Resyncing here,
      // right after the fire, means every card starts the next round at zero, which is what
      // "already sent to the kitchen" actually means.
      syncCartFromOrder();
      // The round the guest was building is now a real KOT of its own (the server fires
      // everything unfired as a fresh batch — see GuestSessionController.PlaceOrder), so this
      // is where "still picking items" ends.
      state.addingMore = false;
      if (s.order && s.order.pendingStaffConfirmation) { showWaitingScreen(); return; }
      showPlacedScreen(s);
    }).catch(function (err) {
      document.getElementById('processing-overlay').classList.remove('show');
      btn.disabled = false;
      btn.textContent = state.addingMore ? 'Send to Kitchen' : 'Place Order';
      if (err.status === 410) { showEnded(err.message || 'This session has ended.'); return; }
      showError(err.message);
    });
  }

  function requestBill() {
    fetchJson(sessionBase + '/request-bill', { method: 'POST' }).then(function (s) {
      showBillScreen(s);
    }).catch(function (err) {
      if (err.status === 410) { showEnded(err.message || 'This session has ended.'); return; }
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
      // MRP items (see MenuItem.IsOpenPrice) are priced by the biller at the till, so a
      // customer has no rate to order at — drop them here rather than showing a card whose
      // Add can only ever fail (the guest cart path refuses them too). The POS reads this
      // same endpoint and must still see them, which is why the filter lives here.
      state.menu = results[1].filter(function (m) { return !m.isOpenPrice; });
      state.taxRatePct = results[2].taxRatePct;
      state.bestSellers = results[3];

      document.getElementById('business-name').textContent = results[2].businessName || 'CafePOS';

      // Which of the three QRs was scanned (see QrTokenService.ModeFor). Only the delivery one
      // changes anything below; a table or menu-only token behaves exactly as before.
      state.deliveryMode = results[0].mode === 'delivery';

      if (state.deliveryMode) {
        // A delivery QR has no table, and the old rule "no table code means browse only" would
        // otherwise lock the very customer this code exists for out of ordering.
        state.browseOnly = false;
        // The cart is local from the first tap, so it needs somewhere to live before one.
        state.order = { items: [] };
        document.getElementById('delivery-fields').style.display = 'block';
        // Optional for a seated guest, unavoidable for a delivery: nobody can hand food to an
        // address that isn't there, and the rider needs a number to call.
        document.getElementById('guest-name-label').textContent = 'Your name';
        document.getElementById('guest-phone-label').textContent = 'Mobile number';
        document.getElementById('locate-btn').onclick = requestLocation;
        // Both of these act on a guest session — "Add more items" appends to an open table tab,
        // "Request Bill" locks it for payment. A delivery order is placed once and settled with
        // the rider, so neither has anything to act on and both are hidden rather than left to
        // fail against a session that was never created.
        document.getElementById('add-more-btn').style.display = 'none';
        document.getElementById('request-bill-btn').style.display = 'none';
      } else {
        state.browseOnly = !state.table.code;
      }

      document.getElementById('table-line').textContent = state.deliveryMode
        ? 'Home delivery'
        : (state.table.code
          ? ('Table ' + state.table.code + ' · ' + state.table.seats + ' seats')
          : 'Browsing the menu');
      document.getElementById('place-btn').onclick = placeOrder;
      document.getElementById('join-btn').onclick = joinSession;
      document.getElementById('add-more-btn').onclick = function () {
        state.addingMore = true;
        // Belt-and-suspenders alongside placeOrder's own resync: whatever's in state.order
        // right now is authoritative for what's actually still unfired, so re-derive
        // state.cart from it before the menu renders off that map.
        syncCartFromOrder();
        showMenuScreen();
      };
      document.getElementById('request-bill-btn').onclick = requestBill;
      document.getElementById('past-lookup-btn').onclick = lookupPastBills;
      // Passive: this only reads positions and toggles a class, so it must never be allowed to
      // hold up the scroll it's watching.
      window.addEventListener('scroll', syncActiveChip, { passive: true });
      document.getElementById('cart-summary-btn').onclick = function () {
        if (cartSheetOpen()) closeCartSheet(); else openCartSheet();
      };
      document.getElementById('cart-sheet-close').onclick = closeCartSheet;
      document.getElementById('cart-sheet-place').onclick = function () { closeCartSheet(); placeOrder(); };
      // Tapping the dimmed area behind the sheet closes it, the same gesture the options
      // sheet already teaches — but only that area, never a tap inside the sheet itself.
      document.getElementById('cart-overlay').onclick = function (e) {
        if (e.target === this) closeCartSheet();
      };
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

      if (state.deliveryMode) {
        // Straight to the menu. doScan below is the table/session handshake — it claims a seat,
        // resolves JOIN/STAFF_ASSIST and starts the 5s poll, none of which exists without a
        // table, and calling it here would fail against a table code that matches no row.
        document.getElementById('loading').style.display = 'none';
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

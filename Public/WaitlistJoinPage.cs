namespace CafePOS.Api.Public;

/// <summary>
/// The customer-facing "join the waitlist" page: one self-contained HTML document (inline
/// CSS/JS, no build step) served by WaitlistPageController at GET /waitlist/{token}. Reads
/// the encrypted token from the URL client-side and posts Name/Phone/PartySize to
/// PublicController.JoinWaitlist. No queue position or count is shown back — just a
/// thank-you message — staff work the actual list from TableManagementScreen's Waiting tab.
/// </summary>
public static class WaitlistJoinPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1" />
<title>Join Waitlist · CafePOS</title>
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
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 16px;
  }
  .card {
    width: 100%;
    max-width: 420px;
    background: var(--card);
    border-radius: 16px;
    padding: 28px 24px;
    box-shadow: 0 2px 12px rgba(43, 24, 16, 0.08);
  }
  h1 { margin: 0 0 4px; font-size: 22px; font-weight: 800; text-align: center; }
  .sub { margin: 0 0 24px; color: var(--muted); font-size: 14px; text-align: center; }
  label { display: block; font-size: 13px; font-weight: 700; margin: 16px 0 6px; }
  input {
    width: 100%;
    padding: 12px 14px;
    border-radius: 10px;
    border: 1px solid var(--divider);
    background: var(--input-tint);
    font-size: 15px;
    color: var(--heading);
  }
  input::placeholder { color: var(--placeholder); }
  input:focus { outline: 2px solid var(--accent); outline-offset: 1px; }
  button {
    width: 100%;
    margin-top: 24px;
    padding: 14px;
    border: none;
    border-radius: 10px;
    background: var(--button);
    color: #fff;
    font-size: 16px;
    font-weight: 700;
    cursor: pointer;
  }
  button:disabled { opacity: 0.6; }
  .banner {
    margin-top: 16px;
    padding: 10px 12px;
    border-radius: 10px;
    font-size: 13px;
    font-weight: 600;
    display: none;
  }
  .banner.error { display: block; background: var(--danger-bg); color: var(--danger); }
  .done { text-align: center; }
  .done .tick {
    width: 56px; height: 56px; border-radius: 50%;
    background: var(--success-bg); color: var(--success);
    display: flex; align-items: center; justify-content: center;
    font-size: 28px; margin: 0 auto 16px;
  }
  .done h1 { margin-bottom: 8px; }
  .done p { color: var(--muted); font-size: 14px; margin: 0; }
  .hidden { display: none; }
</style>
</head>
<body>
  <div class="card">
    <div id="form-view">
      <h1>Join the Waitlist</h1>
      <p class="sub">Enter your details and we'll seat you as soon as a table is free.</p>

      <label for="name">Name</label>
      <input id="name" type="text" placeholder="Your name" maxlength="60" autocomplete="name" />

      <label for="phone">Phone number</label>
      <input id="phone" type="tel" placeholder="10-digit mobile number" maxlength="10" autocomplete="tel" inputmode="numeric" />

      <label for="party">Total Guests</label>
      <input id="party" type="number" placeholder="e.g. 4" min="1" max="50" value="2" />

      <div id="error-banner" class="banner"></div>

      <button id="submit-btn" type="button">Join Waitlist</button>
    </div>

    <div id="done-view" class="done hidden">
      <div class="tick">&#10003;</div>
      <h1>You're on the list</h1>
      <p>Thanks! We'll call you on your phone once a table is ready.</p>
    </div>
  </div>

<script>
(function () {
  var token = decodeURIComponent(location.pathname.split('/').pop());
  var apiUrl = '/api/public/' + encodeURIComponent(token) + '/waitlist';

  var nameEl = document.getElementById('name');
  var phoneEl = document.getElementById('phone');
  var partyEl = document.getElementById('party');
  var errorEl = document.getElementById('error-banner');
  var btn = document.getElementById('submit-btn');

  function showError(msg) {
    errorEl.textContent = msg;
    errorEl.classList.add('error');
    errorEl.style.display = 'block';
  }
  function clearError() {
    errorEl.style.display = 'none';
    errorEl.textContent = '';
  }

  btn.addEventListener('click', function () {
    clearError();

    var name = nameEl.value.trim();
    var phone = phoneEl.value.replace(/\D/g, '');
    var partySize = parseInt(partyEl.value, 10);

    if (!name) return showError('Please enter your name.');
    if (phone.length !== 10) return showError('Enter a 10-digit mobile number.');
    if (!partySize || partySize < 1) return showError('Enter how many guests are in your party.');

    btn.disabled = true;
    btn.textContent = 'Submitting…';

    fetch(apiUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: name, phone: phone, partySize: partySize }),
    }).then(function (res) {
      if (!res.ok) {
        return res.json().catch(function () { return null; }).then(function (body) {
          throw new Error((body && (body.title || body.detail)) || 'Something went wrong. Please try again.');
        });
      }
      document.getElementById('form-view').classList.add('hidden');
      document.getElementById('done-view').classList.remove('hidden');
    }).catch(function (err) {
      btn.disabled = false;
      btn.textContent = 'Join Waitlist';
      showError(err.message);
    });
  });
})();
</script>
</body>
</html>
""";
}

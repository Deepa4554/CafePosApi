namespace CafePOS.Api.Public;

/// <summary>
/// A tiny in-browser PDF viewer served at GET /order/{token} when a cafe's general
/// (menu-only) QR points at an enabled PDF menu (see PublicOrderPageController). It renders
/// the PDF to canvas with pdf.js rather than handing the browser the raw file — because a raw
/// application/pdf response is DOWNLOADED, not shown, on Android Chrome and on any desktop
/// browser with "download PDFs instead of opening" turned on. Rendering to canvas shows the
/// menu inline everywhere, independent of that setting.
///
/// Like CustomerOrderPage, it's one static document for every cafe: the encrypted token is
/// read client-side from the URL path and used to fetch the bytes from the same-origin,
/// anonymous serve endpoint (PublicController.GetMenuPdf). The one departure from that page's
/// "no external requests" rule is pdf.js itself, loaded from a CDN — inlining the whole
/// library would bloat this file by ~1 MB; if a fully self-hosted build is ever needed, drop
/// pdf.min.js/pdf.worker.min.js next to the API and point the two src URLs below at them.
/// </summary>
public static class MenuPdfViewerPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Menu</title>
<style>
  html, body { margin: 0; padding: 0; background: #4a4a4a; }
  #status { color: #eee; font-family: system-ui, -apple-system, sans-serif; text-align: center; padding: 28px 16px; font-size: 15px; line-height: 1.5; }
  #pages { display: flex; flex-direction: column; align-items: center; gap: 10px; padding: 10px 6px 34px; }
  canvas { width: 100%; max-width: 900px; height: auto; background: #fff; box-shadow: 0 2px 10px rgba(0,0,0,.45); border-radius: 2px; }
</style>
</head>
<body>
<div id="status">Loading menu…</div>
<div id="pages"></div>
<script src="https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.min.js"></script>
<script>
  (function () {
    var statusEl = document.getElementById('status');
    var pagesEl = document.getElementById('pages');
    function fail(msg) { statusEl.style.display = 'block'; statusEl.textContent = msg; }

    var pdfjsLib = window['pdfjsLib'];
    if (!pdfjsLib) { fail('Could not load the menu viewer. Please check your connection and try again.'); return; }
    pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.worker.min.js';

    // /order/{token} -> pull the token, fetch the PDF from the same-origin serve endpoint.
    var parts = location.pathname.split('/order/');
    var token = parts.length > 1 ? parts[1] : '';
    var url = '/api/public/' + encodeURIComponent(token) + '/menu-pdf';

    // Cap the render scale so a big/hi-dpi page doesn't allocate an enormous canvas on a phone.
    var scale = Math.min(2, (window.devicePixelRatio || 1) * 1.5);

    pdfjsLib.getDocument(url).promise.then(function (pdf) {
      statusEl.style.display = 'none';
      var chain = Promise.resolve();
      for (var n = 1; n <= pdf.numPages; n++) {
        (function (pageNum) {
          chain = chain.then(function () {
            return pdf.getPage(pageNum).then(function (page) {
              var viewport = page.getViewport({ scale: scale });
              var canvas = document.createElement('canvas');
              canvas.width = viewport.width;
              canvas.height = viewport.height;
              pagesEl.appendChild(canvas);
              return page.render({ canvasContext: canvas.getContext('2d'), viewport: viewport }).promise;
            });
          });
        })(n);
      }
      return chain;
    }).catch(function () {
      fail('This menu is unavailable right now.');
    });
  })();
</script>
</body>
</html>
""";
}

// Generic OAuth callback helper for oauth.ddvnguyen.com
//
//   /<service>-setup?client_id=xxx&auth_url=yyy
//       → redirects to provider's authorization page
//
//   /<service>-callback?code=zzz
//       → displays all received params so you can copy the code
//
// To set up a new OAuth app, deploy this worker and configure:
//   https://oauth.ddvnguyen.com/<service>-setup?client_id=YOUR_CLIENT_ID&auth_url=https://provider.com/oauth/authorize

export default {
  async fetch(request) {
    const url = new URL(request.url);
    const path = url.pathname.replace(/^\/+/, '') || 'unknown';

    if (path.endsWith('-setup')) {
      const service = path.replace(/-setup$/, '');
      const clientId = url.searchParams.get('client_id');
      const authUrl = url.searchParams.get('auth_url');

      if (!clientId || !authUrl) {
        return new Response(
          `Missing client_id or auth_url.\nSetup URL must be: https://oauth.ddvnguyen.com/${service}-setup?client_id=YOUR_CLIENT_ID&auth_url=PROVIDER_AUTH_URL`,
          { status: 400, headers: { 'Content-Type': 'text/plain' } }
        );
      }

      const redirectUri = `${url.origin}/${service}-callback`;
      const separator = authUrl.includes('?') ? '&' : '?';
      const fullAuthUrl = `${authUrl}${separator}client_id=${encodeURIComponent(clientId)}&redirect_uri=${encodeURIComponent(redirectUri)}&response_type=code`;
      return Response.redirect(fullAuthUrl, 302);
    }

    const params = Object.fromEntries(url.searchParams);

    if (request.headers.get('Accept')?.includes('application/json')) {
      return Response.json({ path, params });
    }

    const rows = Object.entries(params)
      .map(([k, v]) => `<tr><td style="padding:8px;font-weight:bold">${k}</td><td style="padding:8px"><code style="background:#eee;padding:2px 6px;border-radius:3px;user-select:all">${v}</code></td></tr>`)
      .join('');

    const html = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>OAuth Callback — ${path}</title>
  <style>
    body { font-family: monospace; padding: 2rem; max-width: 800px; margin: 0 auto; }
    h2 { border-bottom: 1px solid #ccc; padding-bottom: 0.5rem; }
    table { border-collapse: collapse; width: 100%; margin: 1rem 0; }
    td { border: 1px solid #ccc; }
    pre { background: #f4f4f4; padding: 1rem; border-radius: 4px; overflow-x: auto; }
    .hint { color: #666; font-size: 0.9em; }
  </style>
</head>
<body>
  <h2>OAuth Callback — ${path}</h2>
  <p class="hint">Copy the values below and set them as secrets/env vars, then close this tab.</p>
  ${rows.length ? `<table>${rows}</table>` : '<p><em>No query parameters received.</em></p>'}
  <pre>${JSON.stringify(params, null, 2)}</pre>
</body>
</html>`;

    return new Response(html, { headers: { 'Content-Type': 'text/html; charset=utf-8' } });
  },
};

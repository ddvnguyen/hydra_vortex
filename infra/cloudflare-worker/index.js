import { OAuthClient } from "@makeplane/plane-node-sdk";

// Generic OAuth callback helper for oauth.ddvnguyen.com
//
//   /<service>-setup?client_id=xxx
//       → redirects to provider's authorization page via SDK
//
//   /<service>-callback?app_installation_id=xxx&code=yyy
//       → displays all received params so you can copy app_installation_id

export default {
  async fetch(request) {
    const url = new URL(request.url);
    const path = url.pathname.replace(/^\/+/, '') || 'unknown';

    // Setup: redirect to authorization
    if (path.endsWith('-setup')) {
      const service = path.replace(/-setup$/, '');
      const clientId = url.searchParams.get('client_id');

      if (!clientId) {
        return new Response(
          `Missing client_id.\nSetup URL must be: https://oauth.ddvnguyen.com/${service}-setup?client_id=YOUR_CLIENT_ID`,
          { status: 400, headers: { 'Content-Type': 'text/plain' } }
        );
      }

      const redirectUri = `${url.origin}/${service}-callback`;
      const oauth = new OAuthClient({
        clientId,
        clientSecret: '',
        redirectUri,
      });

      const authUrl = oauth.getAuthorizationUrl('code','');
      return Response.redirect(authUrl, 302);
    }

    // Callback: display received params
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

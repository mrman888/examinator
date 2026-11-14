// Angular dev-server proxy. The Aspire AppHost passes the Web gateway address in EXAMINATOR_WEB_URL.
const webUrl = process.env.EXAMINATOR_WEB_URL;
const config = {};

if (!webUrl) {
  console.warn('EXAMINATOR_WEB_URL is not set, so /api is not proxied. ' +
    'Start everything with: dotnet run --project server/AppHost');
} else {
  let url;
  try {
    url = new URL(webUrl);
  } catch {
    throw new Error('EXAMINATOR_WEB_URL must be an http:// URL on localhost, 127.0.0.1 or [::1].');
  }
  // The gateway runs without authentication in local development, so only loopback targets are allowed.
  if (url.protocol !== 'http:' || !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)) {
    throw new Error('EXAMINATOR_WEB_URL must be an http:// URL on localhost, 127.0.0.1 or [::1].');
  }
  config['/api/**'] = { target: url.origin, changeOrigin: false };
}

export default config;

export const environment = {
  production: true,
  // The deploy workflow may rewrite this literal at build time (SELLORA_API_BASE_URL -> 'https://<host>/api/v1').
  // Interceptors match requests by this value as a URL prefix, so a relative path and an absolute URL both work.
  apiBaseUrl: '/api/v1',
};

// Shared BFF client for FullWorth.Web.
//
// This module is the single target for authenticated finance/banking API access.
// Features receive the client through the app context instead of constructing /bff/* URLs themselves.

const DEFAULT_GET_TTL_MS = 2000;

export function jsonBody(data, method = 'POST') {
  return {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data)
  };
}

export function createApiClient(options = {}) {
  const getSpaceId = options.getSpaceId || (() => '');
  const fetchImpl = options.fetchImpl || ((input, init) => window.fetch(input, init));
  const getTtlMs = Number.isFinite(options.getTtlMs) ? options.getTtlMs : DEFAULT_GET_TTL_MS;
  const pendingGets = new Map();

  function withSpace(path) {
    const raw = String(path || '').replace(/^\//, '');
    const spaceId = String(getSpaceId() || '');
    if (!spaceId) return raw;

    const [base, query = ''] = raw.split('?');
    const params = new URLSearchParams(query);
    if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', spaceId);
    const qs = params.toString();
    return qs ? `${base}?${qs}` : base;
  }

  function url(service, path) {
    if (service !== 'backend' && service !== 'banking') {
      throw new Error(`Unknown FullWorth BFF service: ${service}`);
    }
    return `/bff/${service}/${withSpace(path)}`;
  }

  async function errorFrom(response) {
    let message = String(response.status);
    let detail = null;
    try {
      detail = await response.clone().json();
      message = detail?.message || detail?.error || detail?.title || message;
      if (detail?.detail?.conflict) message += ` (${detail.detail.conflict})`;
    } catch {
      // Keep HTTP status when the body is not JSON.
    }
    const error = new Error(message);
    error.status = response.status;
    error.detail = detail;
    return error;
  }

  function methodOf(options) {
    return String(options?.method || 'GET').toUpperCase();
  }

  function invalidate() {
    pendingGets.clear();
  }

  async function fetchResponse(service, path, requestOptions = {}) {
    const method = methodOf(requestOptions);
    const target = url(service, path);
    const isGet = method === 'GET' && requestOptions.body == null;

    if (!isGet) invalidate();

    if (isGet) {
      const hit = pendingGets.get(target);
      if (hit && Date.now() - hit.at < getTtlMs) {
        const response = await hit.promise;
        return response.clone();
      }
    }

    const request = fetchImpl(target, {
      credentials: 'same-origin',
      ...requestOptions
    });

    if (isGet) {
      pendingGets.set(target, { at: Date.now(), promise: request });
      if (pendingGets.size > 200) {
        const entries = [...pendingGets.entries()]
          .sort((a, b) => a[1].at - b[1].at)
          .slice(0, pendingGets.size - 150);
        entries.forEach(([key]) => pendingGets.delete(key));
      }
    }

    let response;
    try {
      response = await request;
    } catch (error) {
      if (isGet) {
        const hit = pendingGets.get(target);
        if (hit?.promise === request) pendingGets.delete(target);
      }
      throw error;
    }

    if (!response.ok) {
      if (isGet) {
        const hit = pendingGets.get(target);
        if (hit?.promise === request) pendingGets.delete(target);
      }
      throw await errorFrom(response);
    }

    return response.clone();
  }

  async function request(service, path, requestOptions) {
    const response = await fetchResponse(service, path, requestOptions);
    if (response.status === 204) return null;
    const raw = await response.text();
    return raw ? JSON.parse(raw) : null;
  }

  return {
    backend: (path, requestOptions) => request('backend', path, requestOptions),
    banking: (path, requestOptions) => request('banking', path, requestOptions),
    backendResponse: (path, requestOptions) => fetchResponse('backend', path, requestOptions),
    bankingResponse: (path, requestOptions) => fetchResponse('banking', path, requestOptions),
    backendUrl: path => url('backend', path),
    bankingUrl: path => url('banking', path),
    withSpace,
    invalidate
  };
}

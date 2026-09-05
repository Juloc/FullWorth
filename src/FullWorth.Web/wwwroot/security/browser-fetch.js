(() => {
  const nativeFetch = window.fetch.bind(window);
  const unsafeMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
  let token = null;
  let tokenRequest = null;

  async function getToken() {
    if (token) return token;
    if (!tokenRequest) {
      tokenRequest = nativeFetch('/auth/antiforgery', {
        method: 'GET',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: { Accept: 'application/json' }
      }).then(async response => {
        if (!response.ok) throw new Error(`Antiforgery token request failed: ${response.status}`);
        const payload = await response.json();
        if (!payload?.token) throw new Error('Antiforgery token response was invalid.');
        token = payload.token;
        return token;
      }).finally(() => { tokenRequest = null; });
    }
    return tokenRequest;
  }

  function shouldProtect(url, method) {
    return url.origin === location.origin
      && unsafeMethods.has(method)
      && (url.pathname.startsWith('/auth/') || url.pathname.startsWith('/bff/'));
  }

  // Chrome on Android can lose the backing handle for files selected from cloud document providers
  // (notably Google Drive) while a multipart request is being prepared. Copy the bytes into a new
  // browser-owned File before upload so fetch no longer depends on the transient provider handle.
  async function snapshotUploadFile(file) {
    if (!(file instanceof File)) throw new TypeError('Expected a File.');
    const bytes = await file.arrayBuffer();
    return new File([bytes], file.name, {
      type: file.type,
      lastModified: file.lastModified
    });
  }

  window.fetch = async function financeFetch(input, init = {}) {
    const sourceRequest = input instanceof Request ? input : null;
    const method = String(init.method || sourceRequest?.method || 'GET').toUpperCase();
    const url = new URL(sourceRequest?.url || String(input), location.href);
    let requestInit = init;

    if (shouldProtect(url, method)) {
      const headers = new Headers(sourceRequest?.headers || undefined);
      if (init.headers) new Headers(init.headers).forEach((value, key) => headers.set(key, value));
      headers.set('X-CSRF-TOKEN', await getToken());
      requestInit = {
        ...init,
        method,
        headers,
        credentials: init.credentials || sourceRequest?.credentials || 'same-origin'
      };
    }

    const response = await nativeFetch(input, requestInit);
    if (response.ok && (url.pathname === '/auth/login' || url.pathname === '/auth/passkeys/login/complete')) {
      token = null;
    }
    if (response.status === 401 && location.pathname === '/compensation.html' && url.pathname.startsWith('/bff/')) {
      const returnUrl = `${location.pathname}${location.search}${location.hash}`;
      location.assign(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}&status=session-expired`);
    }
    return response;
  };

  window.financeAntiforgery = {
    refresh() {
      token = null;
      return getToken();
    }
  };

  window.financeFileUpload = {
    snapshot: snapshotUploadFile
  };
})();

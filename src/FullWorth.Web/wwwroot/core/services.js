import { createApiClient, jsonBody } from './api.js';
import { createI18n } from './i18n.js';
import { state } from './state.js';

export const apiClient = createApiClient({
  getSpaceId: () => state.space?.id || ''
});

export const api = (path, options) => apiClient.backend(path, options);
export const bankApi = (path, options) => apiClient.banking(path, options);
export const i18n = createI18n({ state });

export { jsonBody };

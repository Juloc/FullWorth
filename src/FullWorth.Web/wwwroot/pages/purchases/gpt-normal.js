import { ensurePurchaseArticlesWorkspace } from './articles-workspace.js';
import { refreshPurchaseAdvancedInstaller } from './articles-advanced.js';
import { refreshPurchasePriceInsights } from './price-insights.js';
import { refreshPurchaseAdvancedInsights } from './advanced-insights.js';

let purchaseLifecycleInstalled = false;

function installPurchaseLifecycle() {
  if (purchaseLifecycleInstalled) return;
  purchaseLifecycleInstalled = true;
  document.addEventListener('fullworth:purchases-ui-changed', event => {
    const detail = event.detail || {};
    void Promise.allSettled([
      refreshPurchaseAdvancedInstaller(detail),
      refreshPurchasePriceInsights(detail),
      refreshPurchaseAdvancedInsights(detail)
    ]);
  });
}

export function initializePurchaseEnhancements() {
  installPurchaseLifecycle();
  ensurePurchaseArticlesWorkspace();
}

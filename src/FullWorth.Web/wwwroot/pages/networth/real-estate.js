import '../../components/accessibility-release.js';
import { refreshSpecializedAssets } from './specialized-assets.js';
import { refreshExtraSpecializedAssets } from './specialized-assets-extra.js';
import { refreshInvestmentConsolidation } from './investment-consolidation.js';
import '../../features/wealth-portability.js';
import { openRealEstateDetail as openCoreRealEstateDetail } from './real-estate-core.js';
import { attachRealEstateOperations } from './real-estate-operations.js';
import { attachRealEstateAdvanced } from './real-estate-advanced.js';

export async function refreshWealthExtensions() {
  await Promise.all([
    refreshSpecializedAssets(),
    refreshExtraSpecializedAssets(),
    refreshInvestmentConsolidation()
  ]);
}

export async function openRealEstateDetail(ctx, asset, onChanged) {
  const before = new Set(document.querySelectorAll('dialog'));
  await openCoreRealEstateDetail(ctx, asset, onChanged);

  const dialogs = [...document.querySelectorAll('dialog')];
  const dlg = dialogs.find(item => !before.has(item) && item.querySelector('.property-dialog'))
    || dialogs.reverse().find(item => item.querySelector('.property-dialog') && item.open);
  if (!dlg) return;

  await attachRealEstateOperations(ctx, dlg, asset, onChanged);
  await attachRealEstateAdvanced(ctx, dlg, asset, onChanged);
}

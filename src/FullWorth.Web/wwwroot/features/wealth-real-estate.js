import '../ui/accessibility-release.js';
import './wealth-specialized-assets.js';
import './wealth-specialized-assets-extra.js';
import './wealth-investment-consolidation.js';
import './wealth-portability.js';
import { openRealEstateDetail as openCoreRealEstateDetail } from './wealth-real-estate-core.js';
import { attachRealEstateOperations } from './wealth-real-estate-operations.js';
import { attachRealEstateAdvanced } from './wealth-real-estate-advanced.js';

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

// Entry point of the browser build. It only starts the runtime and hands the exports to the page;
// nothing here computes anything, so the one formula stays in C#.
import { dotnet } from './_framework/dotnet.js';

const { getAssemblyExports, getConfig } = await dotnet.create();
const exports = await getAssemblyExports(getConfig().mainAssemblyName);
const api = exports.FullWorth.Compensation.Wasm.CompensationApi;

// No network calls, by design: the profile never leaves the tab.
globalThis.fullworthCompensation = {
  calculate: profile => JSON.parse(api.Calculate(JSON.stringify(profile))),
  supportedTaxYears: () => JSON.parse(api.SupportedTaxYears())
};

document.dispatchEvent(new CustomEvent('fullworth-compensation-ready'));

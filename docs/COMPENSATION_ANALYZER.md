# FullWorth – Gehalt & Benefits / Compensation Analyzer

Status: implemented and extended on `feature/compensation-history-timeline`; repository CI/build validation still requires the repository's manual `workflow_dispatch`.

## Goal

`Gehalt & Benefits` is a first-class FullWorth area for understanding employment compensation beyond gross salary. It combines a German 2026 tax-class-aware net-pay planning estimate, employer cost, company car, occupational pension, custom benefits, inflation-adjusted salary negotiation, scenario comparison, optimization and confirmed payslip history.

Calculated payroll values are explicitly estimates. Imported payslip values are treated as actual observations only after the user reviews and confirms the extracted fields.

## Implemented product scope

### 1. Net salary planning calculator
- Monthly or annual gross input.
- 12, 13 or 14 salary payments per year.
- Annual bonus separated from regular salary payments.
- German 2026 income-tax tariff.
- Tax classes I–VI affect the estimated wage-tax result.
- Tax class IV factor procedure input.
- Optional ELStAM annual allowance and child-allowance units.
- Age-aware childless long-term-care surcharge.
- Optional pension/unemployment-insurance exemptions for supported planning cases.
- Class II single-parent relief in the planning model.
- Class III splitting-style wage-tax calculation.
- Class V/VI BMF-PAP threshold/difference method.
- Pension, unemployment, statutory health and long-term-care contributions.
- Configurable GKV additional contribution.
- Federal-state aware church-tax rate.
- Child / childless long-term-care handling including Saxony.
- Child allowance treatment for Soli/church-tax assessment by tax class.
- Annual and monthly estimated cash net.
- Employer total cost.
- Marginal net value of the next EUR 100 gross.
- Effective FullWorth value per working hour.

The wage-tax path follows the 2026 BMF PAP structure for ordinary statutory-insurance employment and is entirely local. It is not presented as a complete payroll engine for every PAP input. Private insurance, Midijob/transition-zone rules, pension/versorgungsbezuege and exact special-payment payroll paths still need dedicated inputs before cent-exact payroll parity can be claimed.

### 2. Company car
- 1.0%, 0.5% and 0.25% valuation factors.
- 0.03% commuting component.
- Employee contribution reducing taxable benefit.
- Optional employer vehicle cost.
- Optional private-car alternative cost.
- Taxable benefit, estimated net cash impact and estimated personal value shown separately.
- FullWorth accounting does not subtract the company-car cash impact twice: cash net already contains the tax/cash effect, while FullWorth adds the separately estimated private-car replacement value.

### 3. Occupational pension (bAV)
- Employee salary conversion.
- Employer contribution.
- 2026 tax/social-insurance exemption limits.
- Current estimated net sacrifice versus invested amount.
- Configurable long-term projection.
- Benefit-efficiency metric.
- FullWorth includes both employee-converted and employer-funded amounts because both become invested pension value while the employee cash sacrifice is already reflected in cash net.

### 4. Other benefits
Custom benefits support:
- employer monthly cost,
- personal monthly value,
- taxable monthly value,
- employee monthly cost.

Examples: Deutschlandticket, JobRad, meal subsidy, childcare, VL and stock benefits.

### 5. Employer-cost / FullWorth view
Separately exposes:
- contractual gross,
- employer social contributions,
- employer bAV,
- employer company-car cost,
- employer benefit cost,
- total employer cost,
- estimated cash net,
- personal benefit value,
- FullWorth compensation value.

### 6. Inflation and salary negotiation
- Bundled German CPI index with source/as-of metadata.
- Previous salary and date.
- Purchasing-power-maintenance salary.
- Nominal versus real salary change.
- Desired salary nominal/real change.
- Difference between pure inflation compensation and a true real raise.
- Optional additional real responsibility/market adjustment.

The module does not invent market-salary benchmarks. External salary-market data requires a future explicit provider.

### 7. Scenarios and offers
Persist and compare named scenarios such as:
- current contract,
- offer A / offer B,
- 80% / 90% part time,
- salary versus bAV,
- private car versus company car.

Comparison exposes net, employer cost, FullWorth value and effective hourly value instead of one opaque score.

### 8. Optimizer
`POST /api/compensation/insights` calculates:
- +3%, +5%, +10% salary cases,
- 90% and 80% working-time cases,
- a fixed employer-budget comparison between additional gross, employer bAV and a simulated tax-free benefit.

The tax-free benefit option is explicitly a comparison simulation; eligibility of a concrete benefit depends on the applicable tax rules.

### 9. Payslip analysis and salary history
Implemented in the first release:
- PDF/JPG/PNG/WEBP/TIFF/BMP upload.
- Local-only extraction in the backend container using `pdftoppm` + Tesseract (`deu+eng`).
- Temporary files are deleted after processing; the original payslip is not intentionally persisted.
- Parser detects period, gross, net, payout, wage tax, solidarity surcharge, church tax, RV, AV, KV, PV, company-car benefit, bAV and bonus/extra payment.
- Confidence score and warnings are returned.
- User must review/correct extracted fields before persistence.
- Confirmed structured monthly observations are persisted per user and finance space.
- Latest two months can be compared with component-level explanations for net changes.
- Saved observations form the first salary/payslip history.

### 10. Effective-dated compensation history

Compensation changes are stored as dated events rather than overwriting the past.

Supported event categories include:
- salary,
- tax,
- marriage / family / child,
- working time,
- benefits,
- company car,
- occupational pension,
- insurance,
- job change,
- combined / other.

Each event stores only the fields changed at that point in time as a JSON merge-style patch. This means a change remains effective until that same field is changed again. Historical entries can be edited, moved to another effective date or deleted; unrelated later changes remain intact.

The timeline API resolves the effective profile for each date and calculates:
- contractual gross,
- estimated net,
- employer cost,
- personal benefits / total compensation value,
- taxes and social insurance,
- effective hourly value,
- marginal net from the next EUR 100 gross,
- company-car cash impact,
- purchasing-power-maintenance gross,
- nominal, inflation and real salary change.

The UI provides:
- a dedicated `Verlauf` tab,
- graph lines for gross, net, purchasing-power maintenance and total compensation value,
- event markers,
- 1/3/5-year and full-history filters,
- per-event financial deltas,
- annual comparison table.

## Backend architecture

Namespace: `FullWorth.Backend.Modules.Compensation`

Main files:
- `CompensationModels.cs`
- `GermanCompensationCalculator.cs`
- `InflationIndex.cs`
- `CompensationInsights.cs`
- `CompensationStore.cs`
- `CompensationEndpoints.cs`
- `PayslipModels.cs`
- `PayslipExtraction.cs`
- `PayslipStore.cs`
- `PayslipEndpoints.cs`

## API

Base: `/api/compensation`

Planning / analysis:
- `POST /calculate`
- `POST /compare`
- `POST /negotiation`
- `POST /insights`
- `GET /inflation`
- `GET /history?fullWorthSpaceId=...`
- `POST /history?fullWorthSpaceId=...`
- `PUT /history/{id}?fullWorthSpaceId=...`
- `DELETE /history/{id}?fullWorthSpaceId=...`
- `GET /timeline?fullWorthSpaceId=...&from=...&to=...`

Profile / scenarios:
- `GET /profile?fullWorthSpaceId=...`
- `PUT /profile?fullWorthSpaceId=...`
- `GET /scenarios?fullWorthSpaceId=...`
- `POST /scenarios?fullWorthSpaceId=...`
- `PUT /scenarios/{id}?fullWorthSpaceId=...`
- `DELETE /scenarios/{id}?fullWorthSpaceId=...`

Payslips:
- `POST /payslips/extract`
- `GET /payslips?fullWorthSpaceId=...`
- `GET /payslips/latest-delta?fullWorthSpaceId=...`
- `POST /payslips?fullWorthSpaceId=...`
- `DELETE /payslips/{id}?fullWorthSpaceId=...`

Every data API requires the trusted authenticated backend user context.

## Persistence and privacy

Compensation data is intentionally private to the user even inside a shared finance space.

### `compensation_profiles`
- `(fullworth_space_id, user_id)` primary key
- JSONB profile payload
- timestamps

### `compensation_scenarios`
- UUID primary key
- fullworth-space id
- user id
- name
- JSONB profile payload
- timestamps

### `compensation_payslips`
- UUID primary key
- fullworth-space id
- user id
- period
- JSONB confirmed structured values
- timestamps

Storage always checks fullworth-space membership and queries/mutates by both `fullworth_space_id` and authenticated `user_id`.

The module currently uses idempotent schema initialization to avoid a large unrelated EF snapshot rewrite while the feature stabilizes. Canonical EF mapping/migration can be performed later without changing API contracts.

## 2026 calculation parameters

### Wage / income tax
BMF PAP 2026 / EStG §32a:
- Grundfreibetrag: EUR 12,348.
- Zone 2 through EUR 17,799.
- Zone 3 through EUR 69,878.
- 42% zone through EUR 277,825.
- 45% above that threshold.
- Arbeitnehmer-Pauschbetrag: EUR 1,230 (not class VI).
- Sonderausgaben-Pauschbetrag: EUR 36; doubled in class III, not applied in class VI.
- Class-II relief: EUR 4,260 in the current planning model.
- Child allowance 2026: EUR 9,756 full / EUR 4,878 half for Soli/church-tax assessment.
- Class V/VI thresholds: W1 EUR 14,071; W2 EUR 34,939; W3 EUR 222,260.
- Statutory-health component of the wage-tax Vorsorgepauschale uses 7.0% plus half the configured additional contribution, following the PAP tax calculation; actual social-insurance cash deduction continues to use the general 7.3% base share.

### Social insurance
- Health/care contribution ceiling: EUR 69,750/year.
- Pension/unemployment contribution ceiling: EUR 101,400/year.
- Pension employee/employer rate: 9.3% each.
- Unemployment employee/employer rate: 1.3% each.
- Health base share: 7.3% each plus half the configured additional contribution.
- Default configured GKV additional contribution: 2.9%; user-editable because actual fund rates vary.

### bAV
Derived from the pension contribution ceiling:
- tax-free limit: 8% = EUR 8,112/year.
- social-insurance-free limit: 4% = EUR 4,056/year.

## Inflation data

Bundled German CPI values carry source and `asOf` metadata. Completed years use annual averages; 2026 calculations use final monthly values bundled with the release. Provisional values are not used as final values.

## UI

Standalone responsive surface: `/compensation.html`.

Tabs:
- Rechner
- Benefits
- Gehaltsgespräch
- Szenarien
- Optimierer
- Lohnabrechnungen
- Verlauf

The normal FullWorth sidebar contains `Gehalt & Benefits`; mobile receives the same entry in the More sheet. The page uses the existing authenticated BFF and shared browser antiforgery wrapper. If a BFF call returns 401 on the compensation page, the browser is redirected to login with a return URL.

The static HTML shell itself contains no compensation values. Salary/profile/scenario/payslip data is accessible only through authenticated BFF/backend calls.

## Tests implemented

Backend deterministic tests cover:
- 2026 ESt tariff boundaries/formula,
- tax-class I BMF reference vector within one cent per month,
- expected net ordering for classes III / I / V,
- social-insurance ceilings,
- childless care contribution,
- company-car taxable benefit,
- company-car FullWorth double-count regression,
- bAV limits,
- bAV FullWorth invested-value regression,
- inflation purchasing-power adjustment,
- salary-negotiation nominal/real split,
- scenario comparison,
- optimizer salary/part-time/budget behavior,
- German payslip text parsing and confidence warnings.

Web baseline coverage verifies that the compensation shell, extension assets and BFF routes are present without internal service URLs.

## Acceptance status

Implemented:
- [x] estimated annual/monthly net from gross salary
- [x] tax classes I–VI affect estimated wage tax/net
- [x] company-car analysis
- [x] bAV analysis
- [x] generic benefits
- [x] employer total cost
- [x] FullWorth personal compensation value
- [x] inflation-adjusted salary comparison
- [x] salary-negotiation mode
- [x] persistent private profiles
- [x] persistent private scenarios
- [x] two-scenario comparison
- [x] optimizer
- [x] local payslip OCR/extraction
- [x] confirmed payslip history
- [x] month-over-month net explanation
- [x] desktop/mobile entry point
- [x] deterministic backend tests
- [x] static self-review and regression fixes
- [x] monthly/annual gross with 12/13/14 payments
- [x] tax class IV factor
- [x] effective-dated salary/tax/family/benefit history
- [x] inflation-aware salary timeline
- [x] annual compensation comparison and per-event deltas

Remaining before merge:
- [ ] run repository `CI` workflow (`workflow_dispatch`) on this branch
- [ ] fix any compiler/test failures reported by CI
- [ ] optional UI/browser smoke pass against a running deployment

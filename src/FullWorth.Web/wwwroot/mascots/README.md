# Mascot sprite contract

Each `<mascot>.svg` uses the same 128×128 scene viewport and exposes SVG `<view>` IDs for `idle`, `happy`, `working`, `warning`, `celebrate`, and `empty`.

Runtime code must not reference these files directly from feature modules. Use `FullWorthAppearance.renderMascotScene(element, scene)` and let `ui/appearance.js` resolve the selected mascot and fallback scene.

A future dedicated semantic scene can be registered with `registerMascotAsset(mascotId, scene, url)` without modifying the feature that requested the scene.

// Der Verweis auf ein Symbol in icons/sprite.svg (#154), fuer <use href>.
//
// Die Adresse steht am <body> (IconSprite.Url in _Layout.cshtml) und traegt den Fingerabdruck dieses
// Releases - dieselbe, auf die die Navigation zeigt, also laedt der Browser das Sprite genau einmal.
export const spriteHref = symbol => `${document.body.dataset.sprite}#${symbol}`;

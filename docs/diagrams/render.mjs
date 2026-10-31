import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { render } from "pele";

// Static exports need their own palette: GitHub displays SVGs as images.
const theme = {
  "--pele-bg": "#101419",
  "--pele-fg": "#edf3f7",
  "--pele-muted": "#a7b5c3",
  "--pele-line": "#8b9dac",
  "--pele-surface": "#1c252f",
  "--pele-surface-alt": "#151d25",
  "--pele-border": "#536573",
  "--pele-accent": "#34d399",
  "--pele-font": "Arial, sans-serif",
  "--pele-radius": "10px",
};
const style = Object.entries(theme).map(([key, value]) => `${key}:${value}`).join(";");

function standaloneSvg(svg) {
  svg = svg.replace(/(<svg\b[^>]*\bstyle=")/, `$1${style};background:#101419;border-radius:16px;`);
  const root = svg.match(/<svg\b[^>]*>/)[0];
  const variables = { ...Object.fromEntries([...root.matchAll(/(--[\w-]+):([^;"]+)/g)].map(m => [m[1], m[2]])), ...theme };
  const resolve = (value, depth = 0) => {
    if (depth > 8) throw new Error("Circular SVG palette variable");
    return value.replace(/var\((--[\w-]+)(?:,([^()]*))?\)/g, (_, key, fallback) => {
      if (!(key in variables) && fallback === undefined) throw new Error(`Unknown SVG variable: ${key}`);
      return resolve(variables[key] ?? fallback, depth + 1);
    });
  };
  // Resolve Pele's palette for image viewers without CSS custom-property support.
  return resolve(svg).replace(/(<svg\b[^>]*>)/, '$1<rect width="100%" height="100%" rx="16" fill="#101419"/>');
}

for (const name of ["workflow-evolution", "architecture"]) {
  const source = readFileSync(new URL(`${name}.mmd`, import.meta.url), "utf8");
  const result = render(source, {
    idPrefix: `evoflow-${name}`,
    fontFamily: "Arial, sans-serif",
    fontSize: 16,
    padding: 32,
    maxWidth: 1040,
    autoDirection: false,
    links: false,
    images: false,
  });
  const svg = standaloneSvg(result.svg);
  if (/<script\b|<foreignObject\b|\bon\w+\s*=|(?:href|src)=["']https?:/i.test(svg)) {
    throw new Error(`Unexpected active or remote content in ${name}`);
  }
  const output = new URL(`../images/${name}.svg`, import.meta.url);
  writeFileSync(output, `${svg}\n`);
  console.log(`${fileURLToPath(output)}: ${Math.round(result.width)} × ${Math.round(result.height)}`);
}

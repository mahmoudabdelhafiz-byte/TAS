// Builds the demo portal into a folder of static files (default ../dist-demo) that any web host can serve,
// also from a sub-folder: no server, no database. Run: npm run build:demo
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const web = resolve(here, '..');
const out = resolve(process.argv[2] ?? resolve(web, 'dist-demo'));
const { build } = await import(process.env.ESBUILD_PATH ?? 'esbuild');

rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

await build({
  entryPoints: [resolve(here, 'demo.js')],
  outfile: resolve(out, 'app.js'),
  bundle: true, format: 'esm', jsx: 'automatic', minify: true, target: 'es2022', legalComments: 'none',
  define: { 'process.env.NODE_ENV': '"production"', 'import.meta.env.DEV': 'false', 'import.meta.env.PROD': 'true' },
  nodePaths: [resolve(web, 'node_modules')],
  plugins: [{
    name: 'raw-text',
    setup(b) {
      b.onResolve({ filter: /\?raw$/ }, a => ({ path: resolve(a.resolveDir, a.path.replace(/\?raw$/, '')), namespace: 'raw' }));
      b.onLoad({ filter: /.*/, namespace: 'raw' }, a => ({ contents: readFileSync(a.path, 'utf8'), loader: 'text' }));
    },
  }, {
    // The fonts come from Google Fonts in index.html, so the demo needs no font files.
    name: 'fonts-from-web',
    setup(b) {
      b.onResolve({ filter: /^@fontsource\// }, a => ({ path: a.path, namespace: 'no-font' }));
      b.onLoad({ filter: /.*/, namespace: 'no-font' }, () => ({ contents: '', loader: 'css' }));
    },
  }],
});

const fonts = 'https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600&family=IBM+Plex+Sans+Arabic:wght@400;500;600&display=swap';
const html = readFileSync(resolve(web, 'index.html'), 'utf8')
  .replace('<title>Yardsyncs</title>', '<title>Yardsyncs — demo</title>\n    <meta name="robots" content="noindex" />\n'
    + `    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />\n    <link rel="stylesheet" href="${fonts}" />\n`
    + '    <link rel="stylesheet" href="./app.css" />')
  .replace('<script type="module" src="/src/main.tsx"></script>', '<script type="module" src="./app.js"></script>');
writeFileSync(resolve(out, 'index.html'), html);
// Apache (most shared hosting): browsers fetch fresh files after an update (skipped where the module is off).
writeFileSync(resolve(out, '.htaccess'), '<IfModule mod_headers.c>\n  <FilesMatch "\\.(html|js|css)$">\n    Header set Cache-Control "no-cache"\n  </FilesMatch>\n</IfModule>\n');
console.log(`Demo built in ${out}`);

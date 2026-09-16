import { copyFile, mkdir, readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const projectRoot = dirname(dirname(fileURLToPath(import.meta.url)));

const bufferDestination = join(projectRoot, 'wwwroot/js/vendor/buffer/buffer.min.js');
await mkdir(dirname(bufferDestination), { recursive: true });
await build({
  entryPoints: [join(projectRoot, 'node_modules/buffer/index.js')],
  bundle: true,
  minify: true,
  format: 'iife',
  globalName: 'BufferPolyfill',
  footer: { js: 'globalThis.Buffer = BufferPolyfill.Buffer;' },
  outfile: bufferDestination
});
console.log('Bundled buffer browser polyfill -> wwwroot/js/vendor/buffer/buffer.min.js');

const assets = [
  {
    from: 'node_modules/isomorphic-git/index.umd.min.js',
    to: 'wwwroot/js/vendor/isomorphic-git/index.umd.min.js'
  },
  {
    from: 'node_modules/isomorphic-git/http/web/index.umd.js',
    to: 'wwwroot/js/vendor/isomorphic-git/http-web.umd.js'
  },
  {
    from: 'node_modules/@isomorphic-git/lightning-fs/dist/lightning-fs.min.js',
    to: 'wwwroot/js/vendor/lightning-fs/lightning-fs.min.js'
  }
];

for (const asset of assets) {
  const source = join(projectRoot, asset.from);
  const destination = join(projectRoot, asset.to);
  await mkdir(dirname(destination), { recursive: true });

  if (await filesAreEqual(source, destination)) {
    console.log(`Skipped unchanged ${asset.to}`);
    continue;
  }

  await copyFile(source, destination);
  console.log(`Copied ${asset.from} -> ${asset.to}`);
}

async function filesAreEqual(leftPath, rightPath) {
  try {
    const [left, right] = await Promise.all([
      readFile(leftPath),
      readFile(rightPath)
    ]);

    return left.equals(right);
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return false;
    }

    throw error;
  }
}

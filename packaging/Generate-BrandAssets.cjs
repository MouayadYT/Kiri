// Run with Node.js and sharp available (locally installed or through NODE_PATH).
// The canonical artwork is docs/images/kiri-logo.svg; all branded raster assets come from it.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');
const root = path.resolve(__dirname, '..');

async function main() {
  const source = await fs.readFile(path.join(root, 'docs/images/kiri-logo.svg'));
  const png = size => sharp(source, { density: 384 }).resize(size, size).png().toBuffer();
  async function save(file, bytes) {
    await fs.mkdir(path.dirname(path.join(root, file)), { recursive: true });
    await fs.writeFile(path.join(root, file), bytes);
  }

  // Modern Windows supports PNG-compressed ICO frames, with 0 denoting 256 pixels.
  const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
  const frames = await Promise.all(sizes.map(png));
  const directory = Buffer.alloc(6 + sizes.length * 16);
  directory.writeUInt16LE(1, 2);
  directory.writeUInt16LE(sizes.length, 4);
  let offset = directory.length;
  frames.forEach((frame, i) => {
    const entry = 6 + i * 16;
    directory[entry] = directory[entry + 1] = sizes[i] % 256;
    directory.writeUInt16LE(1, entry + 4);
    directory.writeUInt16LE(32, entry + 6);
    directory.writeUInt32LE(frame.length, entry + 8);
    directory.writeUInt32LE(offset, entry + 12);
    offset += frame.length;
  });
  const ico = Buffer.concat([directory, ...frames]);
  for (const name of ['Assistant.ico', 'AskAssistant.dark.ico', 'AskAssistant.light.ico']) {
    await save(`src/Assistant.ExplorerExtension/Assets/${name}`, ico);
  }
  for (const size of [16, 32, 48, 128]) {
    await save(`src/Assistant.BrowserBridge/extension/icons/icon-${size}.png`, await png(size));
  }
  await save('src/Assistant.ExplorerExtension/Assets/PackageLogo.png', await png(256));
  await save('src/Assistant.UI/Assets/AskAssistantMark.png', await png(256));
  await save('docs/images/kiri-logo.png', await png(512));

  // Keep the README wordmark and icon aligned inside one SVG: GitHub strips inline CSS,
  // and HTML image alignment does not center the visible capital letters on the artwork.
  const headingIcon = source.toString('utf8').replace('width="100%"', 'width="72"').replace('height="100%"', 'height="72"');
  await save('docs/images/kiri-heading.svg', Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="160" height="72" viewBox="0 0 160 72" role="img" aria-label="Kiri">\n` +
    `<title>Kiri</title>\n${headingIcon}\n` +
    `<text x="82" y="47" font-family="Segoe UI, Arial, sans-serif" font-size="32" font-weight="600" fill="#0774AC">Kiri</text>\n</svg>\n`));

  // Inno Setup accepts uncompressed 24-bit BMPs across supported compiler versions.
  async function wizard(file, width, height, iconSize, top) {
    const rgb = await sharp({ create: { width, height, channels: 3, background: '#ffffff' } })
      .composite([{ input: await png(iconSize), left: Math.floor((width - iconSize) / 2), top }])
      .removeAlpha().raw().toBuffer();
    const stride = Math.ceil(width * 3 / 4) * 4;
    const bmp = Buffer.alloc(54 + stride * height);
    bmp.write('BM');
    bmp.writeUInt32LE(bmp.length, 2);
    bmp.writeUInt32LE(54, 10);
    bmp.writeUInt32LE(40, 14);
    bmp.writeInt32LE(width, 18);
    bmp.writeInt32LE(height, 22);
    bmp.writeUInt16LE(1, 26);
    bmp.writeUInt16LE(24, 28);
    bmp.writeUInt32LE(stride * height, 34);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const src = (y * width + x) * 3;
      const dst = 54 + (height - y - 1) * stride + x * 3;
      bmp[dst] = rgb[src + 2]; bmp[dst + 1] = rgb[src + 1]; bmp[dst + 2] = rgb[src];
    }
    await save(file, bmp);
  }
  await wizard('packaging/branding/wizard-small.bmp', 58, 58, 58, 0);
  await wizard('packaging/branding/wizard-image.bmp', 164, 314, 140, 50);
  console.log('Generated app, Explorer, browser, capture, README, and installer branding.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });

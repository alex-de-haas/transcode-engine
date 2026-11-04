import { execFileSync } from 'node:child_process';
import { readFileSync, realpathSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { sha256 } from './context.mjs';

/** Record the actual host compiler/SDK environment alongside .NET's resolved native packages. */
export function nativeToolchain({ platform = process.platform, run = execFileSync, read = readFileSync,
  realpath = realpathSync, env = process.env, list = readdirSync } = {}) {
  const command = (file, args) => run(file, args, { encoding: 'utf8', maxBuffer: 4 * 1024 * 1024 }).trim();
  const file = path => ({ path, sha256: sha256(read(path)) });
  const result = { platform, runnerImage: env.ImageOS ?? null, runnerImageVersion: env.ImageVersion ?? null,
    sdk: command('dotnet', ['--info']) };
  if (platform === 'darwin') {
    const compiler = command('xcrun', ['--find', 'clang']), linker = command('xcrun', ['--find', 'ld']);
    const sdk = command('xcrun', ['--show-sdk-path']);
    return { ...result, compiler: { ...file(realpath(compiler)), version: command(compiler, ['--version']) },
      linker: file(realpath(linker)), sdkSettings: file(join(sdk, 'SDKSettings.json')),
      os: command('sw_vers', []), targetSdk: command('xcrun', ['--show-sdk-version']) };
  }
  if (platform === 'linux') {
    const compiler = realpath(command('which', ['clang']));
    const linkerName = command(compiler, ['-print-prog-name=ld']);
    const linker = realpath(linkerName.startsWith('/') ? linkerName : command('which', [linkerName]));
    return { ...result, compiler: { ...file(compiler), version: command(compiler, ['--version']) }, linker: file(linker),
      os: read('/etc/os-release', 'utf8'), systemPackages: command('dpkg-query', ['-W', '-f=${binary:Package}=${Version}\n']).split('\n').sort() };
  }
  if (platform === 'win32') {
    const programFiles = env['ProgramFiles(x86)'];
    if (!programFiles) throw new Error('Windows native toolchain root is missing.');
    const installation = command(join(programFiles, 'Microsoft Visual Studio/Installer/vswhere.exe'),
      ['-latest', '-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath']);
    if (!installation) throw new Error('Native AOT C++ toolchain was not found.');
    const version = read(join(installation, 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt'), 'utf8').trim();
    const tools = join(installation, 'VC/Tools/MSVC', version, 'bin/Hostx64/x64');
    const sdkRoot = join(programFiles, 'Windows Kits/10/Lib');
    const sdk = list(sdkRoot).filter(v => /^10\.\d+\.\d+\.\d+$/.test(v)).sort((a,b) => a.localeCompare(b, 'en', { numeric: true })).at(-1);
    if (!sdk) throw new Error('Windows native SDK was not found.');
    return { ...result, compiler: file(join(tools, 'cl.exe')), linker: file(join(tools, 'link.exe')), vcVersion: version,
      windowsSdk: sdk, libraries: ['um/x64/kernel32.lib', 'ucrt/x64/ucrt.lib'].map(path => file(join(sdkRoot, sdk, path))) };
  }
  throw new Error('Unsupported native build host.');
}

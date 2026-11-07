// Runs the .NET y-websocket client (samples/YjsClient) against the JS y-websocket server and checks
// from a JS client that the document and awareness state flow in both directions.
// Usage: node check-dotnet-client.mjs [path-to-YjsClient-dll-or-empty-for-dotnet-run] [port]
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import * as Y from 'yjs'
import { WebsocketProvider } from 'y-websocket'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '..', '..')
const clientDll = process.argv[2] ? path.resolve(process.argv[2]) : ''
const port = Number(process.argv[3] || 1245)
const room = 'dotnet-client-' + Date.now()
let failures = 0
const check = (name, cond, extra) => {
  if (!cond) { failures++; console.error('FAIL', name, extra ?? '') } else console.log('ok  ', name)
}
const waitFor = (fn, what, timeout = 15000) => new Promise((resolve, reject) => {
  const start = Date.now()
  const tick = () => {
    if (fn()) return resolve()
    if (Date.now() - start > timeout) return reject(new Error('timeout waiting for ' + what))
    setTimeout(tick, 20)
  }
  tick()
})
const run = (cmd, args, opts = {}) => new Promise((resolve, reject) => {
  const p = spawn(cmd, args, { cwd: repoRoot, ...opts })
  let stdout = '', stderr = ''
  p.stdout.on('data', d => { stdout += d })
  p.stderr.on('data', d => { stderr += d })
  p.on('error', reject)
  p.on('close', code => resolve({ code, stdout, stderr }))
})

const server = spawn(process.execPath, [path.join(here, 'node_modules/y-websocket/bin/server.cjs')],
  { env: { ...process.env, HOST: 'localhost', PORT: String(port) }, stdio: ['ignore', 'pipe', 'inherit'] })
await new Promise(resolve => server.stdout.on('data', d => { if (String(d).includes('running')) resolve() }))

try {
  const wsUrl = `ws://localhost:${port}`
  const doc = new Y.Doc()
  const provider = new WebsocketProvider(wsUrl, room, doc, { WebSocketPolyfill: WebSocket, disableBc: true })
  await new Promise((resolve, reject) => {
    provider.on('sync', s => { if (s) resolve() })
    provider.on('connection-error', reject)
  })
  doc.getText('t').insert(0, 'js:')
  doc.getMap('m').set('from', 'js')
  provider.awareness.setLocalStateField('user', { name: 'js' })

  const seenAwareness = new Set()
  provider.awareness.on('change', () => {
    for (const [id, s] of provider.awareness.getStates()) if (s?.user?.name === 'dotnet') seenAwareness.add(id)
  })

  const args = clientDll ? [clientDll] : ['run', '--project', 'samples/YjsClient', '--']
  const result = await run('dotnet', [...args, wsUrl, room, 'dotnet'])
  check('.NET client exited cleanly', result.code === 0, result.stderr)
  const lastLine = result.stdout.trim().split('\n').pop()
  let out
  try { out = JSON.parse(lastLine) } catch { }
  check('.NET client printed JSON', !!out, result.stdout)
  if (out) {
    check('.NET client received JS text before appending', out.text === 'js:dotnet', out.text)
    check('.NET client saw JS awareness', Object.values(out.awareness).some(s => s?.user?.name === 'js'), JSON.stringify(out.awareness))
    check('.NET client saw its own awareness', out.awareness[String(out.clientID)]?.user?.name === 'dotnet')
    await waitFor(() => doc.getText('t').toString() === 'js:dotnet', 'JS doc receives .NET edit')
    check('JS client received .NET edit', doc.getText('t').toString() === 'js:dotnet')
    check('JS client received .NET awareness while connected', seenAwareness.has(out.clientID), [...seenAwareness])
    // the .NET client announced a null state on dispose
    await waitFor(() => !provider.awareness.getStates().has(out.clientID), '.NET awareness removed')
    check('.NET awareness removed after disconnect', true)
  }
  provider.destroy()
} catch (e) {
  failures++
  console.error('FAIL', e)
} finally {
  server.kill()
}
console.log(failures === 0 ? 'all .NET client checks passed' : `${failures} check(s) failed`)
process.exit(failures === 0 ? 0 : 1)

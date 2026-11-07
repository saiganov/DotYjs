// Connects two real y-websocket clients to a running DotYjs ASP.NET Core server (samples/YjsServer)
// and verifies document sync, awareness and server-side edits.
// Usage: node check-websocket-server.mjs [ws://localhost:5000/collab] [http://localhost:5000]
import * as Y from 'yjs'
import { WebsocketProvider } from 'y-websocket'

const wsUrl = process.argv[2] ?? 'ws://localhost:5000/collab'
const httpUrl = process.argv[3] ?? 'http://localhost:5000'
const room = 'interop-' + Date.now()
let failures = 0
const check = (name, cond, extra) => {
  if (!cond) { failures++; console.error('FAIL', name, extra ?? '') } else console.log('ok  ', name)
}
const waitFor = (fn, what, timeout = 5000) => new Promise((resolve, reject) => {
  const start = Date.now()
  const tick = () => {
    if (fn()) return resolve()
    if (Date.now() - start > timeout) return reject(new Error('timeout waiting for ' + what))
    setTimeout(tick, 20)
  }
  tick()
})
const connect = doc => {
  const provider = new WebsocketProvider(wsUrl, room, doc, { WebSocketPolyfill: WebSocket, disableBc: true })
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('sync timeout')), 5000)
    provider.on('sync', synced => { if (synced) { clearTimeout(timer); resolve(provider) } })
    provider.on('connection-error', e => { clearTimeout(timer); reject(e) })
  })
}

try {
  const doc1 = new Y.Doc()
  const p1 = await connect(doc1)
  check('client 1 synced', p1.synced)
  doc1.getText('t').insert(0, 'hello')

  const doc2 = new Y.Doc()
  const p2 = await connect(doc2)
  await waitFor(() => doc2.getText('t').toString() === 'hello', 'client 2 receives state')
  check('client 2 received initial state', doc2.getText('t').toString() === 'hello')

  doc2.getText('t').insert(5, ' world')
  doc2.getMap('m').set('k', { nested: [1, 2, 3] })
  await waitFor(() => doc1.getText('t').toString() === 'hello world' && doc1.getMap('m').has('k'), 'client 1 receives update')
  check('client 1 received update', doc1.getText('t').toString() === 'hello world')
  check('nested map value relayed', JSON.stringify(doc1.getMap('m').get('k')) === '{"nested":[1,2,3]}')

  // server-side state via REST
  const serverText = await (await fetch(`${httpUrl}/api/docs/${room}/text/t`)).text()
  check('server holds the same text', serverText === 'hello world', serverText)

  // awareness
  p1.awareness.setLocalStateField('user', { name: 'alice' })
  await waitFor(() => p2.awareness.getStates().get(doc1.clientID)?.user?.name === 'alice', 'awareness relayed')
  check('awareness relayed to client 2', true)
  const serverAwareness = await (await fetch(`${httpUrl}/api/docs/${room}/awareness`)).json()
  check('server tracks awareness', serverAwareness[String(doc1.clientID)]?.user?.name === 'alice', JSON.stringify(serverAwareness))

  // server-side edit is broadcast
  await fetch(`${httpUrl}/api/docs/${room}/text/t`, { method: 'POST', body: '!' })
  await waitFor(() => doc1.getText('t').toString() === 'hello world!' && doc2.getText('t').toString() === 'hello world!', 'server edit broadcast')
  check('server edit broadcast to both clients', true)

  // disconnect removes awareness
  p1.destroy()
  await waitFor(() => !p2.awareness.getStates().has(doc1.clientID), 'awareness removed on disconnect')
  check('awareness removed on disconnect', true)

  // reconnect a fresh client: full state from server
  const doc3 = new Y.Doc()
  const p3 = await connect(doc3)
  await waitFor(() => doc3.getText('t').toString() === 'hello world!', 'late joiner state')
  check('late joiner receives full state', true)
  p2.destroy(); p3.destroy()
} catch (e) {
  failures++
  console.error('FAIL', e)
}
if (failures > 0) { console.error(`${failures} failure(s)`); process.exit(1) }
console.log('y-websocket client interop verified against the .NET server')
process.exit(0)

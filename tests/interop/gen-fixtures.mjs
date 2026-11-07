// Generates binary fixtures with the real JS yjs library so the .NET port can be checked
// for wire compatibility. Run: node gen-fixtures.mjs  (writes fixtures/*.json)
import * as Y from 'yjs'
import * as awarenessProtocol from 'y-protocols/awareness'
import * as syncProtocol from 'y-protocols/sync'
import * as encoding from 'lib0/encoding'
import * as decoding from 'lib0/decoding'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const dir = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures')
fs.mkdirSync(dir, { recursive: true })

const b64 = u8 => Buffer.from(u8).toString('base64')
// root type names => kind, so that a fresh doc can instantiate the typed roots before toJSON()
const roots = doc => Object.fromEntries([...doc.share.entries()].map(([k, v]) => [k, v.constructor.name.replace(/^_/, '')]))
export const touchRoots = (doc, roots) => {
  for (const [name, kind] of Object.entries(roots)) {
    ({ YText: () => doc.getText(name), YMap: () => doc.getMap(name), YArray: () => doc.getArray(name), YXmlFragment: () => doc.getXmlFragment(name), YXmlElement: () => doc.getXmlElement(name) })[kind]()
  }
}

const scenarios = {
  text_basic (doc) {
    const t = doc.getText('t')
    t.insert(0, 'hello world')
    t.delete(5, 6)
    t.insert(5, ', yjs 🙂')
  },
  text_formatting (doc) {
    const t = doc.getText('t')
    t.insert(0, 'hello world')
    t.format(0, 5, { bold: true })
    t.format(3, 4, { italic: true, color: '#f00' })
    t.insertEmbed(3, { image: 'x.png' })
    t.insert(11, 'tail', { bold: null })
    t.setAttribute('meta', { a: 1 })
  },
  map_values (doc) {
    const m = doc.getMap('m')
    m.set('int', 42)
    m.set('neg', -7)
    m.set('float', 1.5)
    m.set('big', 12345678901.25)
    m.set('str', 'héllo')
    m.set('bool', true)
    m.set('null', null)
    m.set('obj', { a: [1, 2, { b: 'c' }], d: null })
    m.set('bin', new Uint8Array([1, 2, 3, 255]))
    m.set('replace', 1)
    m.set('replace', 2)
    m.set('deleted', 'x')
    m.delete('deleted')
  },
  array_nested (doc) {
    const a = doc.getArray('a')
    a.push([1, 'two', true, null, { k: 'v' }, [1, 2]])
    a.insert(2, ['inserted'])
    a.delete(0, 1)
    const sub = new Y.Map()
    a.push([sub])
    sub.set('inner', new Y.Array())
    sub.get('inner').push(['deep'])
    const t = new Y.Text()
    a.push([t])
    t.insert(0, 'nested text')
  },
  xml (doc) {
    const f = doc.getXmlFragment('x')
    const p = new Y.XmlElement('p')
    f.insert(0, [p])
    p.setAttribute('class', 'a')
    p.setAttribute('id', 'z')
    const txt = new Y.XmlText()
    p.insert(0, [txt])
    txt.insert(0, 'hi there')
    txt.format(0, 2, { b: {} })
    const div = new Y.XmlElement('div')
    f.push([div])
    div.insert(0, [new Y.XmlText('x')])
    f.delete(1, 1)
  },
  many_ops (doc) {
    const t = doc.getText('t')
    for (let i = 0; i < 200; i++) {
      t.insert(i % 7, 'ab')
      if (i % 3 === 0) t.delete(0, 1)
    }
    const a = doc.getArray('a')
    for (let i = 0; i < 100; i++) a.insert(i % 5, [i])
    for (let i = 0; i < 30; i++) a.delete(i, 1)
  }
}

const isMain = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)
const out = {}
if (isMain) {
for (const [name, fn] of Object.entries(scenarios)) {
  const doc = new Y.Doc()
  doc.clientID = 1000 + Object.keys(out).length
  const updates = []
  doc.on('update', u => updates.push(b64(u)))
  fn(doc)
  // nosemgrep: javascript.lang.correctness.no-stringify-keys -- fixture keys are plain names, not stringified objects
  out[name] = {
    clientID: doc.clientID,
    roots: roots(doc),
    updateV1: b64(Y.encodeStateAsUpdate(doc)),
    updateV2: b64(Y.encodeStateAsUpdateV2(doc)),
    convertedV1ToV2: b64(Y.convertUpdateFormatV1ToV2(Y.encodeStateAsUpdate(doc))),
    convertedV2ToV1: b64(Y.convertUpdateFormatV2ToV1(Y.encodeStateAsUpdateV2(doc))),
    stateVector: b64(Y.encodeStateVector(doc)),
    incrementalUpdates: updates,
    mergedIncremental: b64(Y.mergeUpdates(updates.map(u => Buffer.from(u, 'base64')))),
    json: JSON.stringify(doc.toJSON()),
    textDelta: doc.share.has('t') ? JSON.stringify(doc.getText('t').toDelta()) : null,
    xml: doc.share.has('x') ? doc.getXmlFragment('x').toString() : null
  }
}

// two-client concurrent scenario
{
  const d1 = new Y.Doc(); d1.clientID = 1
  const d2 = new Y.Doc(); d2.clientID = 2
  d1.getText('t').insert(0, 'abc')
  Y.applyUpdate(d2, Y.encodeStateAsUpdate(d1))
  d1.getText('t').insert(1, 'X')
  d2.getText('t').insert(1, 'Y')
  d2.getText('t').delete(0, 1)
  d1.getMap('m').set('k', 'from1')
  d2.getMap('m').set('k', 'from2')
  const u1 = Y.encodeStateAsUpdate(d1)
  const u2 = Y.encodeStateAsUpdate(d2)
  Y.applyUpdate(d1, u2)
  Y.applyUpdate(d2, u1)
  const d21 = new Y.Doc()
  Y.applyUpdate(d21, u2)
  Y.applyUpdate(d21, u1)
  d21.getText('t'); d21.getMap('m')
  out.concurrent = {
    json21: JSON.stringify(d21.toJSON()),
    roots: roots(d1),
    update1: b64(u1),
    update2: b64(u2),
    merged: b64(Y.mergeUpdates([u1, u2])),
    finalV1: b64(Y.encodeStateAsUpdate(d1)),
    json: JSON.stringify(d1.toJSON()),
    text: d1.getText('t').toString()
  }
}

// mergeUpdates of many updates in mixed order, including the full (garbage-collected) state of the document:
// the order of equal structs depends on yjs sorting its decoders with a stable sort
{
  let seed = 7
  const rnd = n => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed % n }
  const docs = [1, 2, 3].map(clientID => { const d = new Y.Doc(); d.clientID = clientID; return d })
  const updates = []
  docs.forEach(d => d.on('update', (u, origin) => {
    updates.push(u)
    if (!docs.includes(origin)) docs.forEach(other => { if (other !== d) Y.applyUpdate(other, u, d) })
  }))
  for (let i = 0; i < 200; i++) {
    const d = docs[i % 3]
    if (i % 7 === 3) { const a = d.getArray('a'); a.insert(rnd(a.length + 1), [i]); continue }
    const t = d.getText('t'); const len = t.length
    if (i % 5 === 4 && len > 0) { const idx = rnd(len); t.delete(idx, Math.min(1 + rnd(4), len - idx)) } else t.insert(rnd(len + 1), String.fromCharCode(97 + rnd(26)))
  }
  const state = Y.encodeStateAsUpdate(docs[0])
  out.mergeOrder = Object.fromEntries(Object.entries({
    stateFirst: [state, ...updates],
    stateMiddle: [...updates.slice(0, 100), state, ...updates.slice(100)],
    stateLast: [...updates, state]
  }).map(([name, list]) => [name, {
    updates: list.map(b64),
    merged: b64(Y.mergeUpdates(list)),
    mergedV2: b64(Y.mergeUpdatesV2(list.map(u => Y.convertUpdateFormatV1ToV2(u))))
  }]))
}

// y-protocols: sync messages, awareness updates, relative positions
{
  const doc = new Y.Doc(); doc.clientID = 7
  const t = doc.getText('t')
  t.insert(0, 'hello world')
  t.insert(5, ',')
  const peer = new Y.Doc(); peer.clientID = 8
  peer.getText('t').insert(0, 'xyz')
  Y.applyUpdate(peer, Y.encodeStateAsUpdate(doc, Y.encodeStateVector(peer)))
  const step1 = encoding.createEncoder(); syncProtocol.writeSyncStep1(step1, doc)
  const step2 = encoding.createEncoder(); syncProtocol.writeSyncStep2(step2, doc, Y.encodeStateVector(peer))
  const upd = encoding.createEncoder(); syncProtocol.writeUpdate(upd, Y.encodeStateAsUpdate(peer, Y.encodeStateVector(doc)))
  // reply computed by y-protocols when receiving doc's step1 at peer
  const reply = encoding.createEncoder()
  syncProtocol.readSyncMessage(decoding.createDecoder(encoding.toUint8Array(step1)), reply, peer, 'test')

  const aw = new awarenessProtocol.Awareness(doc)
  aw.setLocalState({ user: { name: 'js', color: '#ff0000' }, cursor: null, n: 1.5, arr: [1, 'a', null] })
  const aw2 = new awarenessProtocol.Awareness(peer)
  aw2.setLocalState({ user: { name: 'peer' } })
  const awUpdate = awarenessProtocol.encodeAwarenessUpdate(aw, [doc.clientID])
  awarenessProtocol.applyAwarenessUpdate(aw2, awUpdate, 'remote')
  const awAll = awarenessProtocol.encodeAwarenessUpdate(aw2, Array.from(aw2.getStates().keys()).sort((a, b) => a - b))
  const awarenessState = JSON.stringify(aw.getLocalState())
  const awarenessAllStates = JSON.stringify(Object.fromEntries(aw2.getStates()))
  aw2.setLocalState(null)
  const awRemoved = awarenessProtocol.encodeAwarenessUpdate(aw2, [peer.clientID])
  aw.destroy(); aw2.destroy()

  out.protocols = {
    clientID: doc.clientID,
    peerClientID: peer.clientID,
    docUpdate: b64(Y.encodeStateAsUpdate(doc)),
    peerUpdate: b64(Y.encodeStateAsUpdate(peer)),
    syncStep1: b64(encoding.toUint8Array(step1)),
    syncStep2: b64(encoding.toUint8Array(step2)),
    syncUpdate: b64(encoding.toUint8Array(upd)),
    syncStep1Reply: b64(encoding.toUint8Array(reply)),
    peerText: peer.getText('t').toString(),
    awarenessUpdate: b64(awUpdate),
    awarenessState,
    awarenessAll: b64(awAll),
    awarenessAllStates,
    awarenessRemoved: b64(awRemoved),
    relativePositions: [0, 3, 12].flatMap(i => [0, -1].map(assoc => {
      const rpos = Y.createRelativePositionFromTypeIndex(t, i, assoc)
      return { index: i, assoc, encoded: b64(Y.encodeRelativePosition(rpos)), json: JSON.stringify(Y.relativePositionToJSON(rpos)) }
    }))
  }
}

fs.writeFileSync(path.join(dir, 'js-fixtures.json'), JSON.stringify(out, null, 1))
console.log('wrote', Object.keys(out).length, 'fixtures to', dir)
}

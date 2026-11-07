// Reads updates produced by the .NET test-suite (dotnet-fixtures.json) and verifies that the JS
// yjs library decodes them to the expected content. Run after `dotnet test`.
import * as Y from 'yjs'
import * as awarenessProtocol from 'y-protocols/awareness'
import * as syncProtocol from 'y-protocols/sync'
import * as encoding from 'lib0/encoding'
import * as decoding from 'lib0/decoding'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { touchRoots } from './gen-fixtures.mjs'

const file = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'dotnet-fixtures.json')
const fixtures = JSON.parse(fs.readFileSync(file, 'utf8'))
let failures = 0
const check = (name, cond, extra) => {
  if (!cond) {
    failures++
    console.error('FAIL', name, extra ?? '')
  } else {
    console.log('ok  ', name)
  }
}

for (const [name, f] of Object.entries(fixtures)) {
  const doc = new Y.Doc()
  Y.applyUpdate(doc, Buffer.from(f.updateV1, 'base64'))
  touchRoots(doc, f.roots)
  check(`${name}: v1 json`, JSON.stringify(doc.toJSON()) === f.json, `\n got ${JSON.stringify(doc.toJSON())}\n exp ${f.json}`)
  const doc2 = new Y.Doc()
  Y.applyUpdateV2(doc2, Buffer.from(f.updateV2, 'base64'))
  touchRoots(doc2, f.roots)
  check(`${name}: v2 json`, JSON.stringify(doc2.toJSON()) === f.json)
  if (f.textDelta) {
    check(`${name}: delta`, JSON.stringify(doc.getText('t').toDelta()) === f.textDelta, `\n got ${JSON.stringify(doc.getText('t').toDelta())}\n exp ${f.textDelta}`)
  }
  if (f.incrementalUpdates) {
    const doc3 = new Y.Doc()
    for (const u of f.incrementalUpdates) Y.applyUpdate(doc3, Buffer.from(u, 'base64'))
    touchRoots(doc3, f.roots)
    check(`${name}: incremental json`, JSON.stringify(doc3.toJSON()) === f.json)
  }
  // re-encode in JS and compare bytes with what .NET produced: identical structure => identical bytes
  const reenc = Y.encodeStateAsUpdate(doc)
  check(`${name}: v1 bytes identical after JS re-encode`, Buffer.from(reenc).equals(Buffer.from(f.updateV1, 'base64')))
  const reenc2 = Y.encodeStateAsUpdateV2(doc2)
  check(`${name}: v2 bytes identical after JS re-encode`, Buffer.from(reenc2).equals(Buffer.from(f.updateV2, 'base64')))
}
// protocol messages produced by .NET (DotYjs.Protocols) must be understood by y-protocols
{
  const pf = JSON.parse(fs.readFileSync(path.join(path.dirname(file), 'dotnet-protocol-fixtures.json'), 'utf8'))
  const u8 = s => new Uint8Array(Buffer.from(s, 'base64'))
  const doc = new Y.Doc()
  // step1 from .NET → we answer with step2; .NET doc has content we lack, so our step2 is empty-ish, but step1 must parse
  const reply = encoding.createEncoder()
  const t1 = syncProtocol.readSyncMessage(decoding.createDecoder(u8(pf.syncStep1)), reply, doc, 'dotnet')
  check('protocols: syncStep1 type', t1 === syncProtocol.messageYjsSyncStep1)
  check('protocols: syncStep1 reply is step2', decoding.readVarUint(decoding.createDecoder(encoding.toUint8Array(reply))) === syncProtocol.messageYjsSyncStep2)
  const t2 = syncProtocol.readSyncMessage(decoding.createDecoder(u8(pf.syncStep2)), encoding.createEncoder(), doc, 'dotnet')
  check('protocols: syncStep2 applied', t2 === syncProtocol.messageYjsSyncStep2 && doc.getText('t').toString() === pf.text, doc.getText('t').toString())
  const t3 = syncProtocol.readSyncMessage(decoding.createDecoder(u8(pf.syncUpdate)), encoding.createEncoder(), doc, 'dotnet')
  check('protocols: update applied', t3 === syncProtocol.messageYjsUpdate && doc.getText('t').toString() === pf.peerText, doc.getText('t').toString())
  const aw = new awarenessProtocol.Awareness(doc)
  let changed = null
  aw.on('change', (c, origin) => { changed = { ...c, origin } })
  awarenessProtocol.applyAwarenessUpdate(aw, u8(pf.awarenessUpdate), 'dotnet')
  check('protocols: awareness added', changed && changed.added.length === 1 && changed.added[0] === pf.clientID && changed.origin === 'dotnet', JSON.stringify(changed))
  check('protocols: awareness state', JSON.stringify(aw.getStates().get(pf.clientID)) === pf.awarenessState, JSON.stringify(aw.getStates().get(pf.clientID)))
  // same state encoded by JS must give identical bytes (clientID + clock + JSON)
  check('protocols: awareness bytes identical', Buffer.from(awarenessProtocol.encodeAwarenessUpdate(aw, [pf.clientID])).equals(Buffer.from(pf.awarenessUpdate, 'base64')))
  aw.destroy()
  const rpos = Y.decodeRelativePosition(u8(pf.relativePosition))
  const abs = Y.createAbsolutePositionFromRelativePosition(rpos, doc)
  check('protocols: relative position', abs !== null && abs.type === doc.getText('t') && abs.index === pf.relativePositionIndex, JSON.stringify(abs && abs.index))
}

if (failures > 0) {
  console.error(`${failures} failure(s)`)
  process.exit(1)
}
console.log('all .NET-produced updates verified by JS yjs')

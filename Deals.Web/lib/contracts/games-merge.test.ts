import test from "node:test";
import assert from "node:assert/strict";
import {
  normalizeDuplicateGroups,
  normalizeMergeResult,
  parseMergeRequest,
  type DuplicateGroup
} from "./games-merge.ts";

const group = {
  foldedTitle: "portal 2",
  blocked: false,
  members: [
    { gameId: 10, title: "Portal 2", stores: [{ store: "steam", storeGameId: "620" }], steamAppIds: ["620"], blocked: false, blockReason: null },
    { gameId: 11, title: "Portal 2", stores: [{ store: "GOG", storeGameId: "1207658931" }], steamAppIds: [], blocked: false, blockReason: null }
  ]
};

test("normalizeDuplicateGroups: un grupo válido pasa y normaliza tienda y appids", () => {
  const groups = normalizeDuplicateGroups([group]) as DuplicateGroup[];
  assert.equal(groups.length, 1);
  const [first] = groups;
  assert.equal(first?.foldedTitle, "portal 2");
  assert.deepEqual(first?.members.map((member) => member.gameId), [10, 11]);
  // La tienda se canoniza con el vocabulario compartido, no con un mapa propio.
  assert.deepEqual(first?.members[1]?.stores, [{ store: "gog", storeGameId: "1207658931" }]);
  // Un appid "620" sigue siendo texto: el contrato es `string[]`.
  assert.deepEqual(first?.members[0]?.steamAppIds, ["620"]);
});

test("normalizeDuplicateGroups: una respuesta que no es arreglo se rechaza, no es lista vacía", () => {
  assert.equal(normalizeDuplicateGroups({ groups: [group] }), null);
  assert.equal(normalizeDuplicateGroups("nope"), null);
  assert.equal(normalizeDuplicateGroups(undefined), null);
});

test("normalizeDuplicateGroups: un grupo con menos de dos miembros válidos se descarta", () => {
  assert.equal(normalizeDuplicateGroups([{ ...group, members: [group.members[0]] }])?.length, 0);
  // El segundo miembro existe pero es inválido (sin `title`): cae a uno y el grupo se descarta.
  assert.equal(normalizeDuplicateGroups([{ ...group, members: [group.members[0], { gameId: 12 }] }])?.length, 0);
  assert.equal(normalizeDuplicateGroups([{ ...group, members: [] }])?.length, 0);
  assert.equal(normalizeDuplicateGroups([{ foldedTitle: "  ", members: group.members }])?.length, 0);
});

test("normalizeDuplicateGroups: solo `true` literal marca bloqueado un miembro o un grupo", () => {
  const [groups] = normalizeDuplicateGroups([
    { ...group, blocked: "true", members: [group.members[0], { ...group.members[1], blocked: 1, blockReason: "colisión de reseñas" }] }
  ]) as DuplicateGroup[];
  assert.equal(groups?.blocked, false);
  assert.equal(groups?.members[1]?.blocked, false);
  assert.equal(groups?.members[1]?.blockReason, "colisión de reseñas");

  const blockedMember = { ...group.members[1], blocked: true, blockReason: "tiene reseñas" };
  const valid = normalizeDuplicateGroups([{ ...group, members: [group.members[0], blockedMember] }]) as DuplicateGroup[];
  assert.equal(valid[0]?.members[1]?.blocked, true);
  assert.equal(valid[0]?.members[1]?.blockReason, "tiene reseñas");
});

test("normalizeDuplicateGroups: una tienda fuera del vocabulario conserva su texto, no se descarta", () => {
  const [first] = normalizeDuplicateGroups([
    { ...group, members: [group.members[0], { ...group.members[1], stores: [{ store: "ITAD", storeGameId: "abc" }] }] }
  ]) as DuplicateGroup[];
  assert.deepEqual(first?.members[1]?.stores, [{ store: "itad", storeGameId: "abc" }]);
});

test("normalizeMergeResult: `applied: true` es la rama aplicada y cuenta lo movido", () => {
  const result = normalizeMergeResult({ applied: true, blockReason: null, movedExternalIds: 2, movedSteamGames: 1, movedLibraryRows: 0 });
  assert.equal(result?.kind, "applied");
  assert.equal(result?.kind === "applied" ? result.movedExternalIds : null, 2);
});

test("normalizeMergeResult: `applied: false` es el bloqueo con su razón, no un error de red", () => {
  const result = normalizeMergeResult({ applied: false, blockReason: "Los dos juegos tienen appids de Steam distintos", movedExternalIds: 0, movedSteamGames: 0, movedLibraryRows: 0 });
  assert.equal(result?.kind, "blocked");
  assert.equal(result?.kind === "blocked" ? result.blockReason : null, "Los dos juegos tienen appids de Steam distintos");
});

test("normalizeMergeResult: un contador ausente es null, nunca un 0 inventado", () => {
  const result = normalizeMergeResult({ applied: true });
  assert.equal(result?.kind === "applied" ? result.movedExternalIds : "x", null);
  assert.equal(result?.kind === "applied" ? result.movedLibraryRows : "x", null);
});

test("normalizeMergeResult: sin `applied` booleano la respuesta se rechaza", () => {
  assert.equal(normalizeMergeResult({ blockReason: "no" }), null);
  assert.equal(normalizeMergeResult({ applied: "true" }), null);
  assert.equal(normalizeMergeResult(null), null);
});

test("parseMergeRequest: solo viaja la identidad del superviviente", () => {
  assert.deepEqual(parseMergeRequest({ intoGameId: 10 }), { intoGameId: 10 });
  // Campos extra (un cliente viejo enviando dropReviewIds) se ignoran, no rompen la petición.
  assert.deepEqual(parseMergeRequest({ intoGameId: 10, dropReviewIds: [3] }), { intoGameId: 10 });
});

test("parseMergeRequest: un intoGameId inválido rechaza la petición entera", () => {
  assert.equal(parseMergeRequest({ intoGameId: 0 }), null);
  assert.equal(parseMergeRequest({ intoGameId: "abc" }), null);
  assert.equal(parseMergeRequest({}), null);
  assert.equal(parseMergeRequest(null), null);
});

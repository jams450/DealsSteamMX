import test from "node:test";
import assert from "node:assert/strict";
import {
  formatReviewMonth,
  newestReview,
  normalizeReview,
  normalizeReviewList,
  normalizeReviewListResponse,
  parseReviewCreateRequest,
  parseReviewUpdateRequest,
  playStatusOf,
  reviewStatusLabel
} from "./reviews.ts";

const valid = {
  reviewId: 7,
  gameId: 42,
  platform: "gog",
  startedMonth: "2026-03",
  finishedMonth: "2026-05",
  score: 88,
  scoreLabel: "muy bueno",
  isGoty: true,
  status: "completed",
  body: "  Me gustó mucho.  ",
  created: "2026-09-18T10:00:00Z",
  updated: null
};

test("normalizeReview: reseña ausente o malformada se descarta, nunca se inventa", () => {
  assert.equal(normalizeReview(null), null);
  assert.equal(normalizeReview("reseña"), null);
  assert.equal(normalizeReview({}), null);
  assert.equal(normalizeReview({ gameId: 1, platform: "gog" }), null); // sin reviewId
  assert.equal(normalizeReview({ reviewId: 1, platform: "gog" }), null); // sin gameId
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2 }), null); // sin platform
  assert.equal(normalizeReview({ reviewId: 1.5, gameId: 2, platform: "gog" }), null); // id decimal
  assert.equal(normalizeReview({ reviewId: "1", gameId: 2, platform: "gog" }), null); // id texto

  assert.deepEqual(normalizeReviewList(null), []);
  assert.deepEqual(normalizeReviewList({ reviews: [valid] }), []);
  assert.equal(normalizeReviewListResponse({ reviews: [] }), null);
  assert.deepEqual(normalizeReviewListResponse([]), []);
});

test("normalizeReview: forma válida, campos opcionales y cuerpo recortado", () => {
  const review = normalizeReview(valid);
  assert.deepEqual(review, {
    reviewId: 7,
    gameId: 42,
    platform: "gog",
    startedMonth: "2026-03",
    finishedMonth: "2026-05",
    score: 88,
    scoreLabel: "muy bueno",
    isGoty: true,
    status: "completed",
    body: "Me gustó mucho.",
    created: "2026-09-18T10:00:00Z",
    updated: null
  });

  // Los campos opcionales ausentes degradan a null/false sin tumbar la reseña.
  const bare = normalizeReview({ reviewId: 1, gameId: 2, platform: "steam" });
  assert.equal(bare?.score, null);
  assert.equal(bare?.scoreLabel, null);
  assert.equal(bare?.isGoty, false);
  assert.equal(bare?.status, "finished");
  assert.equal(bare?.body, null);
  assert.equal(bare?.created, null);
});

test("normalizeReview: la nota es solo un entero 0..100", () => {
  const withScore = (score: unknown) => normalizeReview({ reviewId: 1, gameId: 2, platform: "steam", score })?.score;

  assert.equal(withScore(0), 0);
  assert.equal(withScore(100), 100);
  assert.equal(withScore(60), 60);
  assert.equal(withScore(55.5), null);
  assert.equal(withScore(101), null);
  assert.equal(withScore(-1), null);
  assert.equal(withScore("60"), null);
  assert.equal(withScore(Number.NaN), null);
  assert.equal(withScore(null), null);
});

test("normalizeReview: el mes es estricto YYYY-MM", () => {
  const withMonth = (startedMonth: unknown) =>
    normalizeReview({ reviewId: 1, gameId: 2, platform: "steam", startedMonth })?.startedMonth;

  assert.equal(withMonth("2026-01"), "2026-01");
  assert.equal(withMonth("2026-12"), "2026-12");
  assert.equal(withMonth("2026-1"), null);
  assert.equal(withMonth("2026-13"), null);
  assert.equal(withMonth("2026-00"), null);
  assert.equal(withMonth("2026/01"), null);
  assert.equal(withMonth("01-2026"), null);
  assert.equal(withMonth(202601), null);
  assert.equal(withMonth(null), null);
});

test("normalizeReview: slug de plataforma abierto (consolas) y alias de tienda; lo imposible se rechaza", () => {
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2, platform: "origin store" }), null); // espacio → no slug
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2, platform: "" }), null);
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2, platform: "switch" })?.platform, "switch");
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2, platform: "3ds" })?.platform, "3ds");
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2, platform: "gog" })?.platform, "gog");
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2, platform: "Epic Games" })?.platform, "epic");
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2, platform: "Battle.net" })?.platform, "battlenet");
});

test("normalizeReview: isGoty solo con el literal true", () => {
  const goty = (isGoty: unknown) =>
    normalizeReview({ reviewId: 1, gameId: 2, platform: "steam", isGoty })?.isGoty;

  assert.equal(goty(true), true);
  assert.equal(goty(false), false);
  assert.equal(goty("true"), false);
  assert.equal(goty(1), false);
  assert.equal(goty(null), false);
});

test("normalizeReviewList: descarta entradas inválidas y conserva las válidas", () => {
  const list = normalizeReviewList([valid, null, { reviewId: 2 }, { ...valid, reviewId: 8, platform: "epic" }]);
  assert.deepEqual(list.map((review) => review.reviewId), [7, 8]);
});

test("parseReviewCreateRequest: exige identidad y rechaza campos presentes inválidos", () => {
  assert.equal(parseReviewCreateRequest(null), null);
  assert.equal(parseReviewCreateRequest({}), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "origin store" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 0, platform: "gog" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", score: 101 }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", score: 60.5 }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", startedMonth: "2026-13" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", isGoty: "sí" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", body: 12 }), null);
  // El estado es obligatorio y solo los tres valores guardados son válidos: "por jugar" no se escribe.
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", status: "backlog" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", status: "Finished" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", status: true }), null);

  assert.deepEqual(parseReviewCreateRequest({ gameId: 42, platform: "epic games", status: "finished" }), {
    gameId: 42,
    platform: "epic",
    startedMonth: null,
    finishedMonth: null,
    score: null,
    isGoty: false,
    status: "finished",
    body: null
  });
  assert.deepEqual(parseReviewCreateRequest({ gameId: 42, platform: "gog", score: 0, isGoty: false, status: "dropped" }), {
    gameId: 42,
    platform: "gog",
    startedMonth: null,
    finishedMonth: null,
    score: 0,
    isGoty: false,
    status: "dropped",
    body: null
  });
});

test("parseReviewUpdateRequest: no exige identidad y limpia campos con null", () => {
  // Sin `status` la petición se rechaza: editar una reseña nunca debe perder el estado de la partida.
  assert.equal(parseReviewUpdateRequest({}), null);
  assert.deepEqual(parseReviewUpdateRequest({ score: 75, body: "  texto  ", status: "completed" }), {
    startedMonth: null,
    finishedMonth: null,
    score: 75,
    isGoty: false,
    status: "completed",
    body: "texto"
  });
  assert.equal(parseReviewUpdateRequest({ score: "75", status: "finished" }), null);
  assert.equal(parseReviewUpdateRequest({ finishedMonth: "2026-1", status: "finished" }), null);
  assert.equal(parseReviewUpdateRequest({ status: "nope" }), null);
});

test("normalizeReview: status desconocido cae a finished, nunca pierde la reseña", () => {
  assert.equal(normalizeReview({ ...valid, status: "dropped" })?.status, "dropped");
  assert.equal(normalizeReview({ ...valid, status: "completed" })?.status, "completed");
  // Un payload viejo sin `status` (o con basura) se lee como terminado: es el default de la columna.
  assert.equal(normalizeReview({ ...valid, status: undefined })?.status, "finished");
  assert.equal(normalizeReview({ ...valid, status: "abandoned" })?.status, "finished");
});

test("playStatusOf: sin reseña es backlog; con reseña, su estado", () => {
  const review = normalizeReview({ ...valid, status: "completed" });
  assert.equal(playStatusOf(review), "completed");
  assert.equal(playStatusOf(null), "backlog");
  assert.equal(reviewStatusLabel("backlog"), "Por jugar");
  assert.equal(reviewStatusLabel("completed"), "Completado 100%");
  assert.equal(reviewStatusLabel("dropped"), "Dropeado");
  assert.equal(reviewStatusLabel("finished"), "Terminado");
});

test("formatReviewMonth: mes legible y valores nulos sin lanzar", () => {
  assert.equal(formatReviewMonth(null), null);
  assert.match(formatReviewMonth("2026-03") ?? "", /2026/);
  assert.equal(formatReviewMonth("no-es-un-mes"), null);
});

// Varias reseñas del mismo juego y plataforma son válidas: la grilla pinta la última escrita.
test("newestReview: gana la escrita más recientemente, con los empates por reviewId", () => {
  const first = { ...valid, reviewId: 7, updated: null, created: "2026-01-10T00:00:00Z" };
  const replay = { ...valid, reviewId: 9, updated: "2030-06-01T00:00:00Z", created: "2026-01-10T00:00:00Z" };
  assert.equal(newestReview([first, replay])?.reviewId, 9);
  // Sin fechas, el id más alto representa al juego.
  assert.equal(newestReview([{ ...first, created: null }, { ...replay, updated: null, created: null }])?.reviewId, 9);
  // `updated` manda sobre `created`: una reseña vieja editada hoy es la última.
  assert.equal(
    newestReview([{ ...first, created: "2030-01-01T00:00:00Z" }, { ...replay, updated: null, created: "2026-01-10T00:00:00Z" }])?.reviewId,
    7
  );
  assert.equal(newestReview([]), null);
});

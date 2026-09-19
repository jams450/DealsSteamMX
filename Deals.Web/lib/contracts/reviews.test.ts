import test from "node:test";
import assert from "node:assert/strict";
import {
  formatReviewMonth,
  newestReview,
  normalizeReview,
  normalizeReviewList,
  normalizeReviewListResponse,
  parseReviewCreateRequest,
  parseReviewUpdateRequest
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
    body: "Me gustó mucho.",
    created: "2026-09-18T10:00:00Z",
    updated: null
  });

  // Los campos opcionales ausentes degradan a null/false sin tumbar la reseña.
  const bare = normalizeReview({ reviewId: 1, gameId: 2, platform: "steam" });
  assert.equal(bare?.score, null);
  assert.equal(bare?.scoreLabel, null);
  assert.equal(bare?.isGoty, false);
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

test("normalizeReview: plataforma fuera del catálogo se rechaza; alias conocidos se aceptan", () => {
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2, platform: "origin" }), null);
  assert.equal(normalizeReview({ reviewId: 1, gameId: 2, platform: "" }), null);
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
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "origin" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 0, platform: "gog" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", score: 101 }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", score: 60.5 }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", startedMonth: "2026-13" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", isGoty: "sí" }), null);
  assert.equal(parseReviewCreateRequest({ gameId: 42, platform: "gog", body: 12 }), null);

  assert.deepEqual(parseReviewCreateRequest({ gameId: 42, platform: "epic games" }), {
    gameId: 42,
    platform: "epic",
    startedMonth: null,
    finishedMonth: null,
    score: null,
    isGoty: false,
    body: null
  });
  assert.deepEqual(parseReviewCreateRequest({ gameId: 42, platform: "gog", score: 0, isGoty: false }), {
    gameId: 42,
    platform: "gog",
    startedMonth: null,
    finishedMonth: null,
    score: 0,
    isGoty: false,
    body: null
  });
});

test("parseReviewUpdateRequest: no exige identidad y limpia campos con null", () => {
  assert.deepEqual(parseReviewUpdateRequest({}), {
    startedMonth: null,
    finishedMonth: null,
    score: null,
    isGoty: false,
    body: null
  });
  assert.deepEqual(parseReviewUpdateRequest({ score: 75, body: "  texto  " }), {
    startedMonth: null,
    finishedMonth: null,
    score: 75,
    isGoty: false,
    body: "texto"
  });
  assert.equal(parseReviewUpdateRequest({ score: "75" }), null);
  assert.equal(parseReviewUpdateRequest({ finishedMonth: "2026-1" }), null);
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

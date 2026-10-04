/** Only Steam publisher names count; developer, ownership and partial names are not evidence. */
export function isUbisoftPublisher(publishers: unknown): boolean {
  return Array.isArray(publishers) && publishers.some(
    (publisher) => typeof publisher === "string" && publisher.trim().toLowerCase() === "ubisoft"
  );
}

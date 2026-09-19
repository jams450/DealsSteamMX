import path from "node:path";

import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  reactStrictMode: true,
  // La imagen de Docker corre `node .next/standalone/server.js`: Next traza en el build solo los archivos
  // que el servidor necesita, así que el runtime no lleva el node_modules completo ni pnpm.
  output: "standalone",
  outputFileTracingRoot: path.join(__dirname)
};

export default nextConfig;

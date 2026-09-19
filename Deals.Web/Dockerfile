# Imagen base compartida por las etapas de instalación y build. pnpm se fija a la misma versión que la
# máquina de desarrollo, así que la imagen resuelve exactamente como el host.
FROM node:24-alpine AS base
WORKDIR /app
RUN npm install --global pnpm@11.26.0 && npm cache clean --force

FROM base AS deps
# Solo el manifiesto, el lockfile y la config de pnpm: cualquier cambio en el código no invalida esta capa.
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
# `--frozen-lockfile` falla si el lockfile no cuadra con package.json, en vez de resolver versiones nuevas
# dentro de la imagen.
RUN pnpm install --frozen-lockfile

FROM base AS build
COPY --from=deps /app/node_modules ./node_modules
COPY . .
# Next copia `.env` al bundle standalone cuando existe. En Docker no llega al contexto (lo corta
# .dockerignore), pero el borrado deja la garantía aquí y no en un archivo que cualquiera puede cambiar: un
# secreto horneado en una capa no se puede quitar con un `rm` posterior.
RUN pnpm build && rm -f .next/standalone/.env .next/standalone/.env.*

# Runtime sin pnpm ni node_modules: `output: "standalone"` (next.config.ts) ya trajo al build lo que el
# servidor usa, así que esta etapa solo copia el servidor trazado, los estáticos y el directorio público.
FROM node:24-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production
ENV PORT=3000
ENV HOSTNAME=0.0.0.0
COPY --from=build /app/.next/standalone ./
COPY --from=build /app/.next/static ./.next/static
# `node` solo es dueño de lo que el servidor puede escribir en caliente (cachés de Next); el resto del código
# queda de solo lectura.
RUN chown -R node:node /app/.next
USER node
EXPOSE 3000
CMD ["node", "server.js"]

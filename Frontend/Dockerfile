# FileVault frontend: Vite build served by nginx
# The API URL is compiled into the bundle, so pass it at build time:
#   docker build --build-arg VITE_API_BASE_URL=http://localhost:3000 -t filevault-frontend ./Frontend
# It's also used for the Content-Security-Policy (see nginx/default.conf.template).

FROM node:22-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
ARG VITE_API_BASE_URL=http://localhost:3000
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
RUN npm run build

FROM nginx:1.27-alpine AS runtime
ARG VITE_API_BASE_URL=http://localhost:3000
# The image turns templates into conf.d/*.conf at start-up, substituting environment variables
ENV API_URL=$VITE_API_BASE_URL
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY --chmod=755 nginx/15-api-origin.envsh /docker-entrypoint.d/15-api-origin.envsh
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80

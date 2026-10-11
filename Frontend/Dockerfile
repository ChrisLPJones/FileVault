# FileVault frontend: Vite build served by nginx
# The API URL is read when the container starts (one image works for any URL):
#   docker build -t filevault-frontend ./Frontend
#   docker run -e API_URL=https://api.example.com -p 5173:80 filevault-frontend
# nginx/15-api-origin.envsh validates it, writes /config.js for the app and derives the
# Content-Security-Policy origin (see nginx/default.conf.template).

FROM node:22-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginx:1.27-alpine AS runtime
# The image turns templates into conf.d/*.conf at start-up, substituting environment variables
ENV API_URL=http://localhost:3000
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY --chmod=755 nginx/15-api-origin.envsh /docker-entrypoint.d/15-api-origin.envsh
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80

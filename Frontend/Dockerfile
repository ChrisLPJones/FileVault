# FileVault frontend: Vite build served by nginx
# The API URL is compiled into the bundle, so pass it at build time:
#   docker build --build-arg VITE_API_BASE_URL=http://localhost:3000 -t filevault-frontend ./Frontend

FROM node:22-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
ARG VITE_API_BASE_URL=http://localhost:3000
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
RUN npm run build

FROM nginx:1.27-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80

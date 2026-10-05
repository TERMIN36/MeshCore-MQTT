FROM node:20-alpine AS build
WORKDIR /src
COPY src/MeshCoreMqtt.Web/package.json src/MeshCoreMqtt.Web/package-lock.json ./
RUN npm ci
COPY src/MeshCoreMqtt.Web ./
RUN npm run build

FROM nginx:1.27-alpine
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY docker/40-access-mode.sh /docker-entrypoint.d/40-access-mode.sh
COPY --from=build /src/dist /usr/share/nginx/html

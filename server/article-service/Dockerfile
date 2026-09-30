FROM node:22-alpine
WORKDIR /app
COPY package.json ./
RUN npm install
COPY server.js fill-cache.js ./
EXPOSE 4001
CMD ["node", "server.js"]

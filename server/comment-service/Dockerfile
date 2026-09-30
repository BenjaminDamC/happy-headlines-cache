FROM node:22-alpine
WORKDIR /app
COPY package.json ./
RUN npm install
COPY server.js ./
EXPOSE 4002
CMD ["node", "server.js"]

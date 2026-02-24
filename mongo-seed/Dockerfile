FROM mongo:7.0
COPY seed.js /seed.js
CMD ["mongosh", "mongodb://mongo:27017/CommerceHub", "/seed.js"]

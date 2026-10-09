// Unsigned JWTs for tests: the app only decodes them (the server checks signatures)
const base64Url = (value) =>
    btoa(JSON.stringify(value)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");

// expiresIn: seconds from now (negative = already expired, null = no exp claim)
export const makeToken = ({ expiresIn = 900, sub = "user-1", id = Math.random().toString(36).slice(2) } = {}) => {
    const payload = { sub, jti: id };
    if (expiresIn !== null) payload.exp = Math.floor(Date.now() / 1000) + expiresIn;
    return `${base64Url({ alg: "HS256", typ: "JWT" })}.${base64Url(payload)}.signature`;
};

// Must match AuthServices.ValidatePassword on the server
export const passwordRules = [
    { label: "At least 8 characters", test: (p) => p.length >= 8 },
    { label: "At least one number", test: (p) => /\d/.test(p) },
    { label: "At least one uppercase letter", test: (p) => /[A-Z]/.test(p) },
    { label: "At least one lowercase letter", test: (p) => /[a-z]/.test(p) },
];

export const meetsPasswordRules = (password) => passwordRules.every((rule) => rule.test(password));

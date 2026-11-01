namespace WristOTP.Server.Models;

// Encrypted account bundle sent by the phone and picked up by the watch via
// the relay. The server never decrypts or interprets it — it only stores and
// returns an opaque blob. Iv/AuthTag/Ciphertext are Base64 strings
// (AES-256-GCM).
//
// Зашифрованный пакет аккаунтов: его отправляет телефон и забирают часы
// через релей. Сервер его не расшифровывает и не разбирает — только хранит и
// отдаёт непрозрачный блоб. Iv/AuthTag/Ciphertext — Base64-строки
// (AES-256-GCM).
public sealed record TokenPayload(string Iv, string AuthTag, string Ciphertext);

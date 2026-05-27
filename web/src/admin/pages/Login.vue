<script setup lang="ts">
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import { adminApi, type ApiError } from '../../shared/api';
import ThemeToggle from '../../shared/ThemeToggle.vue';

const router = useRouter();
const username = ref('');
const password = ref('');
const error = ref<string | null>(null);
const submitting = ref(false);

async function submit() {
  error.value = null;
  submitting.value = true;
  try {
    await adminApi.login(username.value, password.value);
    router.replace({ name: 'dashboard' });
  } catch (err) {
    error.value = (err as ApiError).message ?? 'login failed';
  } finally {
    submitting.value = false;
  }
}
</script>

<template>
  <div class="wrap">
    <div class="theme-corner"><ThemeToggle /></div>
    <form class="card" @submit.prevent="submit">
      <div class="brand">
        <img src="/prism-logo.png" alt="PRISM" class="brand-logo" />
        PRISM admin
      </div>
      <label>Username
        <input v-model="username" autocomplete="username" required />
      </label>
      <label>Password
        <input v-model="password" type="password" autocomplete="current-password" required />
      </label>
      <div v-if="error" class="error-box">{{ error }}</div>
      <button class="primary" :disabled="submitting" type="submit">{{ submitting ? 'Signing in…' : 'Sign in' }}</button>
    </form>
  </div>
</template>

<style scoped>
.wrap {
  min-height: 100vh;
  min-height: 100dvh;
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  box-sizing: border-box;
  position: relative;
  background: var(--color-bg);
}
.theme-corner { position: absolute; top: 16px; right: 16px; }
form {
  width: 100%;
  max-width: 360px;
  padding: 28px;
  display: flex;
  flex-direction: column;
  gap: 14px;
  box-shadow: var(--shadow-2);
}
.brand { display: flex; align-items: center; gap: 8px; font-weight: 700; font-size: 18px; margin-bottom: 6px; }
.brand-logo { width: 32px; height: 32px; object-fit: contain; }
label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--color-text-muted); }
</style>

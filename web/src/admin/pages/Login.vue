<script setup lang="ts">
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import { adminApi, type ApiError } from '../../shared/api';

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
    <form class="card" @submit.prevent="submit">
      <div class="brand">
        <span class="brand-dot"></span>
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
.wrap { display: grid; place-items: center; height: 100vh; }
form { width: 320px; display: flex; flex-direction: column; gap: 12px; }
.brand { display: flex; align-items: center; gap: 8px; font-weight: 700; font-size: 18px; margin-bottom: 6px; }
.brand-dot { width: 10px; height: 10px; background: var(--orbit-primary); border-radius: 50%; }
label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--color-text-muted); }
</style>

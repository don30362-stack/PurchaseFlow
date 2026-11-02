import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { ApiError, apiRequest } from '@/services/api'

export type UserRole = 'Employee' | 'Manager'

export interface AuthUser {
  id: string
  userName: string
  role: UserRole | null
}

export const useAuthStore = defineStore('auth', () => {
  const user = ref<AuthUser | null>(null)
  const initialized = ref(false)
  const isLoading = ref(false)

  const isAuthenticated = computed(() => user.value !== null)
  const role = computed(() => user.value?.role ?? null)

  // 啟動網站或重新整理後，向後端確認登入狀態
  async function initialize(): Promise<void> {
    if (initialized.value) {
      return
    }

    isLoading.value = true

    try {
      user.value = await apiRequest<AuthUser>('/api/auth/me')
      initialized.value = true
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        user.value = null
        initialized.value = true
        return
      }

      throw error
    } finally {
      isLoading.value = false
    }
  }

  // 登入成功後，再取得使用者資料及角色
  async function login(
    userName: string,
    password: string,
  ): Promise<void> {
    isLoading.value = true

    try {
      await apiRequest<{ message: string }>('/api/auth/login', {
        method: 'POST',
        body: {
          userName,
          password,
        },
      })

      user.value = await apiRequest<AuthUser>('/api/auth/me')
      initialized.value = true
    } finally {
      isLoading.value = false
    }
  }

  // 登出後清除前端使用者狀態
  async function logout(): Promise<void> {
    isLoading.value = true

    try {
      await apiRequest<void>('/api/auth/logout', {
        method: 'POST',
      })

      user.value = null
      initialized.value = true
    } catch (error) {
      // Session 已失效時，視為已登出
      if (error instanceof ApiError && error.status === 401) {
        user.value = null
        initialized.value = true
        return
      }

      throw error
    } finally {
      isLoading.value = false
    }
  }

  return {
    user,
    role,
    initialized,
    isLoading,
    isAuthenticated,
    initialize,
    login,
    logout,
  }
})

#!/bin/bash

# 定义旧的标识和新的标识
OLD_EMAIL="393370459@qq.com"
NEW_NAME="sunyingda"
NEW_EMAIL="sunyingda@myhexin.com"

# --- 执行区 ---
# 提醒：这会重写历史，如果仓库很大可能需要一点时间
git filter-branch -f --env-filter "
if [ \"\$GIT_COMMITTER_EMAIL\" = \"$OLD_EMAIL\" ]
then
    export GIT_COMMITTER_NAME=\"$NEW_NAME\"
    export GIT_COMMITTER_EMAIL=\"$NEW_EMAIL\"
fi
if [ \"\$GIT_AUTHOR_EMAIL\" = \"$OLD_EMAIL\" ]
then
    export GIT_AUTHOR_NAME=\"$NEW_NAME\"
    export GIT_AUTHOR_EMAIL=\"$NEW_EMAIL\"
fi
" --tag-name-filter cat -- --branches --tags